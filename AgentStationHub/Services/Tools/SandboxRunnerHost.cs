using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStationHub.Models;
using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Host-side bridge that invokes the AgentStationHub.SandboxRunner CLI inside
/// the Docker sandbox to perform LLM-heavy tasks (deployment planning) using
/// a Microsoft Agent Framework multi-agent team with direct /workspace access.
///
/// Workflow:
///   1. Publish the runner once per process lifetime into a temp folder.
///   2. Bind-mount that folder into the container at /tools/runner.
///   3. 'docker run ... dotnet /tools/runner/AgentStationHub.SandboxRunner.dll'
///   4. Stream stderr (agent trace events) to the live log as they arrive.
///   5. Parse the final JSON RunnerResponse from stdout.
/// </summary>
public sealed class SandboxRunnerHost
{
    private readonly string _openAiEndpoint;
    private readonly string _openAiDeployment;
    // Per-agent-role deployments. When non-null these are forwarded to
    // the SandboxRunner via dedicated env vars and the runner picks the
    // right one for `plan` (strategist), `remediate` (doctor) and
    // `verify` (verifier). Null entries fall back to _openAiDeployment.
    // See AgentStationHub.SandboxRunner/Program.cs#PickDeployment.
    private readonly string? _strategistDeployment;
    private readonly string? _doctorDeployment;
    private readonly string? _verifierDeployment;
    private readonly string? _openAiApiKey;
    private readonly string? _tenantId;

    private static readonly SemaphoreSlim _publishLock = new(1, 1);
    private static string? _publishedPath;

    public SandboxRunnerHost(
        string openAiEndpoint,
        string openAiDeployment,
        string? openAiApiKey,
        string? tenantId,
        string? strategistDeployment = null,
        string? doctorDeployment = null,
        string? verifierDeployment = null)
    {
        _openAiEndpoint = openAiEndpoint;
        _openAiDeployment = openAiDeployment;
        _strategistDeployment = strategistDeployment;
        _doctorDeployment = doctorDeployment;
        _verifierDeployment = verifierDeployment;
        _openAiApiKey = openAiApiKey;
        _tenantId = tenantId;
    }

    public async Task<DeploymentPlan> ExtractPlanAsync(
        string sandboxImage,
        string repoUrl,
        string hostWorkspace,
        string azureLocation,
        Action<string, string> onLog,
        CancellationToken ct,
        IReadOnlyList<AgentInsight>? priorInsights = null)
    {
        var toolsDir = await EnsurePublishedAsync(ct);

        var request = new RunnerRequest
        {
            Command = "plan",
            RepoUrl = repoUrl,
            Workspace = "/workspace",
            AzureLocation = azureLocation,
            PriorInsights = MapInsights(priorInsights)
        };

        var (exit, stdout) = await RunAsync(
            sandboxImage, toolsDir, hostWorkspace, request,
            line => onLog("info", $"[agent-runner] {line}"), ct);

        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                $"Sandbox runner 'plan' failed (exit {exit}). Output: {Truncate(stdout, 500)}");

        var response = JsonSerializer.Deserialize<RunnerResponse>(stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Runner returned empty response.");

        if (!response.Ok || response.Plan is null)
            throw new InvalidOperationException(
                response.Error ?? "Runner returned ok=false with no plan.");

        // Map DTO back to the domain model used by the orchestrator.
        var steps = response.Plan.Steps?
            .Select(s => new DeploymentStep(s.Id, s.Description, s.Cmd, s.Cwd))
            .ToList() ?? new List<DeploymentStep>();

        return new DeploymentPlan(
            repoUrl,
            response.Plan.Prerequisites ?? new List<string>(),
            response.Plan.Env ?? new Dictionary<string, string>(),
            steps,
            response.Plan.VerifyHints ?? new List<string>())
        {
            RepoKind = response.Plan.RepoKind,
            IsDeployable = response.Plan.IsDeployable ?? true,
            NotDeployableReason = response.Plan.NotDeployableReason
        };
    }

    /// <summary>
    /// Asks the DeploymentDoctor agent (inside the sandbox) to analyse a
    /// failed step and propose a remediation. Returns null when the runner
    /// is unreachable or when the remediation is "give_up". The orchestrator
    /// treats null as "no fix available, surface the original error".
    /// </summary>
    public async Task<Remediation?> RemediateAsync(
        string sandboxImage,
        string hostWorkspace,
        DeploymentPlan plan,
        int failedStepId,
        string errorTail,
        IReadOnlyList<string> previousAttempts,
        Action<string, string> onLog,
        CancellationToken ct,
        IReadOnlyList<AgentInsight>? priorInsights = null)
    {
        var toolsDir = await EnsurePublishedAsync(ct);

        var request = new RunnerRequest
        {
            Command = "remediate",
            Workspace = "/workspace",
            FailedStepId = failedStepId,
            ErrorTail = errorTail,
            PreviousAttempts = previousAttempts.ToList(),
            PriorInsights = MapInsights(priorInsights),
            Plan = new PlanDto
            {
                Prerequisites = plan.Prerequisites.ToList(),
                Env = plan.Environment.ToDictionary(kv => kv.Key, kv => kv.Value),
                VerifyHints = plan.VerifyHints.ToList(),
                Steps = plan.Steps
                    .Select(s => new StepDto
                    {
                        Id = s.Id, Description = s.Description,
                        Cmd = s.Command, Cwd = s.WorkingDirectory
                    })
                    .ToList(),
                // Always forward the classification fields. Leaving
                // IsDeployable as null on the wire makes the runner's
                // non-nullable 'bool IsDeployable' property fail
                // deserialisation with a 'Cannot get the value of a token
                // type Null' exception � crashing the Doctor before it can
                // even read the plan.
                IsDeployable = plan.IsDeployable,
                RepoKind = plan.RepoKind,
                NotDeployableReason = plan.NotDeployableReason
            }
        };

        var (exit, stdout) = await RunAsync(
            sandboxImage, toolsDir, hostWorkspace, request,
            line => onLog("info", $"[agent-runner] {line}"), ct);

        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            onLog("err",
                $"Doctor runner exited {exit}. Output: {Truncate(stdout, 300)}");
            return null;
        }

        var response = JsonSerializer.Deserialize<RunnerResponse>(stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (response is null || !response.Ok || response.Remediation is null)
        {
            onLog("err",
                $"Doctor returned no remediation: {response?.Error ?? "(empty)"}");
            return null;
        }

        var r = response.Remediation;
        var newSteps = (r.NewSteps ?? new List<StepDto>())
            .Select(s => new DeploymentStep(s.Id, s.Description, s.Cmd, s.Cwd) { ActionJson = s.ActionJson })
            .ToList();
        return new Remediation(r.Kind ?? "give_up", r.StepId, newSteps, r.Reasoning);
    }

    private async Task<(int ExitCode, string Stdout)> RunAsync(
        string sandboxImage,
        string toolsDir,
        string hostWorkspace,
        RunnerRequest request,
        Action<string> onStderrLine,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--dns", "1.1.1.1",
            "--dns", "8.8.8.8",
            "--memory", "6g",
            "--memory-swap", "8g",
            "-v", $"{hostWorkspace}:/workspace:ro",
            "-v", $"{toolsDir}:/tools/runner:ro",
            "-e", $"AZURE_OPENAI_ENDPOINT={_openAiEndpoint}",
            "-e", $"AZURE_OPENAI_DEPLOYMENT={_openAiDeployment}"
        };
        // Per-role deployment overrides. Forwarded only when set so the
        // runner falls back to AZURE_OPENAI_DEPLOYMENT (legacy single-
        // model behaviour) when nothing is configured.
        if (!string.IsNullOrWhiteSpace(_strategistDeployment))
            args.AddRange(new[] { "-e", $"AZURE_OPENAI_DEPLOYMENT_STRATEGIST={_strategistDeployment}" });
        if (!string.IsNullOrWhiteSpace(_doctorDeployment))
            args.AddRange(new[] { "-e", $"AZURE_OPENAI_DEPLOYMENT_DOCTOR={_doctorDeployment}" });
        if (!string.IsNullOrWhiteSpace(_verifierDeployment))
            args.AddRange(new[] { "-e", $"AZURE_OPENAI_DEPLOYMENT_VERIFIER={_verifierDeployment}" });
        if (!string.IsNullOrWhiteSpace(_openAiApiKey))
            args.AddRange(new[] { "-e", $"AZURE_OPENAI_API_KEY={_openAiApiKey}" });
        if (!string.IsNullOrWhiteSpace(_tenantId))
            args.AddRange(new[] { "-e", $"AZURE_TENANT_ID={_tenantId}" });

        args.AddRange(SandboxAzureAuth.VolumeMountArgs());

        args.Add(sandboxImage);
        args.Add("dotnet");
        args.Add("/tools/runner/AgentStationHub.SandboxRunner.dll");

        var stdinJson = JsonSerializer.Serialize(request);
        var stdoutBuf = new System.Text.StringBuilder();
        int exit = -1;

        var cmd = Cli.Wrap("docker")
            .WithArguments(args)
            .WithStandardInputPipe(PipeSource.FromString(stdinJson))
            .WithValidation(CommandResultValidation.None);

        await foreach (var ev in cmd.ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o:
                    stdoutBuf.AppendLine(o.Text);
                    break;
                case StandardErrorCommandEvent e:
                    onStderrLine(e.Text);
                    break;
                case ExitedCommandEvent x:
                    exit = x.ExitCode;
                    break;
            }
        }
        return (exit, stdoutBuf.ToString());
    }

    /// <summary>
    /// Publishes the AgentStationHub.SandboxRunner project into a temp folder
    /// the first time it is needed, then caches the result. Self-contained is
    /// NOT used: we rely on the .NET 8 runtime pre-installed in the sandbox
    /// image, which keeps the artifact small (~15 MB instead of ~80 MB).
    /// </summary>
    private static async Task<string> EnsurePublishedAsync(CancellationToken ct)
    {
        if (_publishedPath is not null) return _publishedPath;
        await _publishLock.WaitAsync(ct);
        try
        {
            if (_publishedPath is not null) return _publishedPath;

            // Escape hatch for containerised deployments: the Dockerfile
            // pre-publishes the runner during the build stage and exports
            // its path via AGENTICHUB_RUNNER_PATH. In that case we skip
            // the dotnet publish entirely � the container runtime image
            // ships only aspnet, not the SDK, so running 'dotnet publish'
            // at app startup would fail.
            var prepublished = Environment.GetEnvironmentVariable("AGENTICHUB_RUNNER_PATH");
            if (!string.IsNullOrWhiteSpace(prepublished)
                && Directory.Exists(prepublished)
                && Directory.EnumerateFiles(prepublished, "*.dll").Any())
            {
                _publishedPath = prepublished;
                return prepublished;
            }

            var projectPath = FindRunnerProject()
                ?? throw new InvalidOperationException(
                    "Could not locate AgentStationHub.SandboxRunner.csproj. " +
                    "Expected to find it as a sibling of the main project directory, " +
                    "or set AGENTICHUB_RUNNER_PATH to a directory containing a pre-published runner.");

            var outputDir = Path.Combine(Path.GetTempPath(), "agentichub-runner");
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
            Directory.CreateDirectory(outputDir);

            // Dynamic arch selection: the runner must be published for the
            // SAME arch as the sandbox image will run on, and that tracks
            // the Docker daemon (effectively the host). Hardcoding
            // linux-arm64 used to break x64 setups.
            var rid = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "linux-arm64",
                System.Runtime.InteropServices.Architecture.X64   => "linux-x64",
                _ => "linux-x64"
            };

            var result = await Cli.Wrap("dotnet")
                .WithArguments(new[]
                {
                    "publish", projectPath,
                    "-c", "Release",
                    "-r", rid,             // matches the host/sandbox arch
                    "--no-self-contained", // rely on pre-installed .NET runtime
                    "-o", outputDir
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"dotnet publish of SandboxRunner failed (exit {result.ExitCode}).");

            _publishedPath = outputDir;
            return outputDir;
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// Walks up from the running assembly looking for the runner .csproj in
    /// the standard locations: either as a sibling of the current project
    /// directory OR under a 'src/' or directly next to a .sln file. Works
    /// regardless of whether the solution file sits inside or next to the
    /// main project folder.
    /// </summary>
    private static string? FindRunnerProject()
    {
        const string TargetRelative = "AgentStationHub.SandboxRunner"
            + "/AgentStationHub.SandboxRunner.csproj";

        var candidates = new List<string>();
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            candidates.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        candidates.Add(Directory.GetCurrentDirectory());

        foreach (var root in candidates.Distinct())
        {
            var probe = Path.GetFullPath(Path.Combine(root, TargetRelative));
            if (File.Exists(probe)) return probe;
        }
        return null;
    }

    /// <summary>
    /// Converts domain-level <see cref="AgentInsight"/> records into the
    /// wire DTO accepted by the sandbox runner. Caps the payload at 20
    /// highest-confidence entries so the runner's LLM prompt stays below
    /// comfortable context limits � prior insights are meant to be
    /// HINTS, not a full history dump.
    /// </summary>
    private static List<PriorInsightWire>? MapInsights(IReadOnlyList<AgentInsight>? ins)
    {
        if (ins is null || ins.Count == 0) return null;
        return ins
            .OrderByDescending(i => i.Confidence)
            .ThenByDescending(i => i.At)
            .Take(20)
            .Select(i => new PriorInsightWire
            {
                Key = i.Key,
                Value = i.Value,
                Confidence = i.Confidence,
                At = i.At.ToString("O")
            })
            .ToList();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    // ---- JSON DTOs mirroring AgentStationHub.SandboxRunner.Contracts ----

    private sealed class RunnerRequest
    {
        [JsonPropertyName("command")]   public string Command { get; set; } = "";
        [JsonPropertyName("repoUrl")]   public string? RepoUrl { get; set; }
        [JsonPropertyName("workspace")] public string Workspace { get; set; } = "";
        [JsonPropertyName("azureLocation")] public string? AzureLocation { get; set; }
        [JsonPropertyName("plan")]         public PlanDto? Plan { get; set; }
        [JsonPropertyName("failedStepId")] public int? FailedStepId { get; set; }
        [JsonPropertyName("errorTail")]    public string? ErrorTail { get; set; }
        [JsonPropertyName("previousAttempts")] public List<string>? PreviousAttempts { get; set; }
        // Optional cross-session learning signals passed to the planning
        // team. Null on legacy hosts and when the memory store has no
        // entries for the repo yet.
        [JsonPropertyName("priorInsights")] public List<PriorInsightWire>? PriorInsights { get; set; }
    }

    private sealed class PriorInsightWire
    {
        [JsonPropertyName("key")]        public string Key { get; set; } = "";
        [JsonPropertyName("value")]      public string Value { get; set; } = "";
        [JsonPropertyName("confidence")] public double Confidence { get; set; } = 1.0;
        [JsonPropertyName("at")]         public string At { get; set; } = "";
    }

    private sealed class RunnerResponse
    {
        [JsonPropertyName("ok")]    public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("plan")]  public PlanDto? Plan { get; set; }
        [JsonPropertyName("remediation")] public RemediationDto? Remediation { get; set; }
    }

    private sealed class RemediationDto
    {
        [JsonPropertyName("kind")]      public string? Kind { get; set; }
        [JsonPropertyName("stepId")]    public int StepId { get; set; }
        [JsonPropertyName("newSteps")]  public List<StepDto>? NewSteps { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }

    private sealed class PlanDto
    {
        [JsonPropertyName("prerequisites")] public List<string>? Prerequisites { get; set; }
        [JsonPropertyName("env")]           public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("steps")]         public List<StepDto>? Steps { get; set; }
        [JsonPropertyName("verifyHints")]   public List<string>? VerifyHints { get; set; }
        [JsonPropertyName("repoKind")]      public string? RepoKind { get; set; }
        [JsonPropertyName("isDeployable")]  public bool? IsDeployable { get; set; }
        [JsonPropertyName("notDeployableReason")] public string? NotDeployableReason { get; set; }
    }

    private sealed class StepDto
    {
        [JsonPropertyName("id")]          public int Id { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("cmd")]         public string Cmd { get; set; } = "";
        [JsonPropertyName("cwd")]         public string Cwd { get; set; } = ".";
        // Typed-action JSON from the sandbox runner. When non-null the
        // host orchestrator dispatches via ActionRegistry rather than
        // executing Cmd as bash. See Services/Actions/IDeployAction.cs.
        [JsonPropertyName("actionJson")]  public string? ActionJson { get; set; }
    }
}
