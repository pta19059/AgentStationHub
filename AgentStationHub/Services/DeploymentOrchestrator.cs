using System.Collections.Concurrent;
using AgentStationHub.Hubs;
using AgentStationHub.Models;
using AgentStationHub.Services.Agents;
using AgentStationHub.Services.Security;
using AgentStationHub.Services.Tools;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AgentStationHub.Services;

public sealed class DeploymentOrchestrator
{
    private readonly IHubContext<DeploymentHub> _hub;
    private readonly IServiceProvider _sp;
    private readonly DeploymentOptions _opt;
    private readonly ILogger<DeploymentOrchestrator> _log;
    private readonly AgentMemoryStore _memory;
    private readonly DeploymentSessionStore _store;
    private readonly ConcurrentDictionary<string, DeploymentSession> _sessions = new();

    public DeploymentOrchestrator(
        IHubContext<DeploymentHub> hub,
        IServiceProvider sp,
        IOptions<DeploymentOptions> opt,
        ILogger<DeploymentOrchestrator> log,
        AgentMemoryStore memory,
        DeploymentSessionStore store)
    {
        _hub = hub;
        _sp = sp;
        _opt = opt.Value;
        _log = log;
        _memory = memory;
        _store = store;

        // Rehydrate sessions left on disk by a previous app lifetime.
        // The in-process pipeline task cannot be resurrected — the
        // Docker sandbox child died with the app — but the session's
        // status/plan/logs are kept so the user's localStorage-driven
        // resume still lands on a meaningful view (last known state +
        // an explicit "Interrupted" marker) instead of an empty page.
        RehydrateSessionsFromDisk();

        // Plant durable, codebase-authored lessons as GLOBAL insights
        // (RepoUrl == null). These are consulted on every deploy by
        // GetRelevantInsights() and forwarded to the Strategist and
        // Doctor agents, so repeat mistakes the project already knows
        // about do NOT need to be rediscovered by the LLM each time.
        // SeedGlobalInsightIfChanged is a no-op when the value is
        // already stored unchanged, so this is safe to call at every
        // boot.
        SeedBuiltInGlobalInsights();

        // Cleanup of ORPHAN sandbox containers from previous app lifetimes.
        // Classic failure mode: a deploy got stuck on a buildx hang (or
        // the app container crashed / was restarted) and the sandbox
        // sibling kept running on the host daemon. Without this sweep the
        // zombie sandbox holds docker resources, and — more dangerously —
        // still appears "busy" from the user's perspective (Docker Desktop
        // shows "sandbox Up 9 hours" when the deploy was abandoned the day
        // before). On startup we now forcibly kill any container whose
        // image starts with 'agentichub/sandbox:'. This is safe because:
        //   • The _sessions dictionary is always empty at construction
        //     time (singleton service, boots with the process).
        //   • Every live session owns a CancellationToken that would
        //     have cleaned its sandbox; if a sandbox exists at boot
        //     it's guaranteed to be an orphan.
        // Failures of this sweep are non-fatal: the app still starts.
        _ = Task.Run(async () =>
        {
            try
            {
                await CleanOrphanSandboxesAsync("startup orphan sweep");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Orphan sandbox cleanup failed at startup (non-fatal). " +
                    "If you see a stuck 'agentichub/sandbox:*' container, 'docker kill' it manually.");
            }
        });
    }

    /// <summary>
    /// Enumerate and force-kill every container running an
    /// <c>agentichub/sandbox:*</c> image. Used at startup (to reap
    /// orphans from previous app lifetimes) and on explicit session
    /// cancel (to make sure the sandbox sibling dies with the session
    /// and isn't left holding a buildx lock against the host daemon).
    /// </summary>
    private async Task CleanOrphanSandboxesAsync(string reason)
    {
        // List first so we can report what we're killing.
        var listOut = new System.Text.StringBuilder();
        var listResult = await CliWrap.Cli.Wrap("docker")
            .WithArguments(new[]
            {
                "ps", "--filter", "ancestor=agentichub/sandbox:v10",
                "--filter", "ancestor=agentichub/sandbox:v9",
                "--filter", "ancestor=agentichub/sandbox:v8",
                "--format", "{{.ID}} {{.Image}} {{.Status}}"
            })
            .WithValidation(CliWrap.CommandResultValidation.None)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStringBuilder(listOut))
            .ExecuteAsync();

        var lines = listOut.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (lines.Count == 0)
        {
            _log.LogInformation("Sandbox orphan sweep ({Reason}): none found.", reason);
            return;
        }

        _log.LogWarning(
            "Sandbox orphan sweep ({Reason}): killing {Count} zombie sandbox container(s):\n{Details}",
            reason, lines.Count, string.Join("\n", lines));

        foreach (var line in lines)
        {
            var id = line.Split(' ', 2)[0];
            if (string.IsNullOrWhiteSpace(id)) continue;
            try
            {
                await CliWrap.Cli.Wrap("docker")
                    .WithArguments(new[] { "kill", id })
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .WithStandardOutputPipe(CliWrap.PipeTarget.Null)
                    .WithStandardErrorPipe(CliWrap.PipeTarget.Null)
                    .ExecuteAsync();
            }
            catch (Exception killEx)
            {
                _log.LogWarning(killEx, "Failed to kill orphan sandbox {Id}", id);
            }
        }
    }

    /// <summary>
    /// Hard-coded lessons about failure modes this project has already
    /// debugged at least once. Stored as GLOBAL insights (RepoUrl=null)
    /// so they are surfaced to the planning and Doctor agents on every
    /// future deploy regardless of repo. Keep each value SHORT and
    /// ACTIONABLE — they are injected into the agent prompts verbatim.
    /// Update the <c>key</c> suffix (…v2, …v3) when changing the text
    /// to force a re-seed, otherwise SeedGlobalInsightIfChanged will
    /// silently skip the write.
    /// </summary>
    private void SeedBuiltInGlobalInsights()
    {
        // Lesson from Azure-Samples/azure-ai-travel-agents and every
        // other azd + Container Apps monorepo: `azd up` runs provision
        // THEN deploy. The provision phase creates Container Apps with
        // a placeholder image (mcr.microsoft.com/azuredocs/
        // containerapps-helloworld); only the deploy phase does docker
        // build, push to ACR, and revision update. If `azd up` completes
        // but any Container App still shows the hello-world image, the
        // deploy half silently failed or was skipped — the fix is NOT
        // to rerun provision but to run `azd deploy --no-prompt` on its
        // own, optionally scoped to the failing service
        // (`azd deploy <serviceName> --no-prompt`).
        _memory.SeedGlobalInsightIfChanged(
            "azd.containerapps.hello-world-placeholder",
            "When deploying a repo whose azure.yaml targets Azure Container " +
            "Apps: `azd up` provisions first (Container Apps are created with " +
            "the mcr.microsoft.com/azuredocs/containerapps-helloworld placeholder " +
            "image) and only then runs deploy (docker build + ACR push + " +
            "revision update). If the final container apps still serve the " +
            "hello-world image, the deploy half failed — the correct remediation " +
            "is `azd deploy --no-prompt` (NOT another `azd up`). Per-service " +
            "retry: `azd deploy <serviceName> --no-prompt`. Always add an " +
            "explicit `azd deploy --no-prompt` verification step AFTER `azd up " +
            "--no-prompt` when the plan targets Container Apps.",
            confidence: 1.0);

        // Companion lesson for the Doctor: how to detect the condition
        // so it can trigger the fix without relying on the verifier.
        _memory.SeedGlobalInsightIfChanged(
            "azd.containerapps.hello-world-detect",
            "Detect the hello-world trap with: `az containerapp list -g " +
            "<resourceGroup> --query \"[].{name:name,image:properties.template." +
            "containers[0].image}\" -o tsv` — any row whose image contains " +
            "'containerapps-helloworld' means that service's `azd deploy` " +
            "never completed. Fix: `azd deploy --no-prompt` from the repo root " +
            "(the env already has AZURE_SUBSCRIPTION_ID, AZURE_TENANT_ID, " +
            "AZURE_LOCATION set).",
            confidence: 1.0);
    }

    public DeploymentSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Reads every persisted session from disk and registers it in the
    /// in-memory dictionary. Any session whose status was NOT terminal
    /// at the time of the previous shutdown is transitioned to Failed
    /// with an explicit "Interrupted" marker, because the background
    /// pipeline task that was driving it is long gone.
    /// </summary>
    private void RehydrateSessionsFromDisk()
    {
        try
        {
            foreach (var s in _store.LoadAll())
            {
                var wasTerminal = s.Status
                    is DeploymentStatus.Succeeded
                    or DeploymentStatus.Failed
                    or DeploymentStatus.Cancelled
                    or DeploymentStatus.Rejected
                    or DeploymentStatus.NotDeployable
                    or DeploymentStatus.BlockedNeedsHumanOrSourceFix;

                if (!wasTerminal)
                {
                    // The previous process died mid-deploy (app restart,
                    // container recreate, crash). The sandbox sibling is
                    // also gone, so there is nothing to continue. Mark
                    // the session as Failed so the UI stops showing a
                    // spinner and the user knows to restart the run.
                    s.Status = DeploymentStatus.Failed;
                    var hint = "The deploy was interrupted by an app restart " +
                               "before reaching a terminal state. Server-side logs " +
                               "up to the last checkpoint are preserved; please " +
                               "start a new deploy to continue.";
                    s.ErrorMessage = string.IsNullOrWhiteSpace(s.ErrorMessage)
                        ? hint
                        : s.ErrorMessage + " | " + hint;
                    s.Logs.Add(new LogEntry(
                        DateTime.UtcNow, "err",
                        "[orchestrator] " + hint));
                    _store.SaveLater(s);
                }

                _sessions[s.Id] = s;
            }
            _log.LogInformation(
                "Rehydrated {Count} persisted deployment session(s) from {Path}.",
                _sessions.Count, _store.Directory_);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Session rehydration failed (non-fatal). Previous deploys will " +
                "appear as fresh runs instead of resumable handles.");
        }
    }


    public string Start(
        string repoUrl,
        string? azureLocation = null,
        string? samplePath = null,
        string? tenantId = null,
        string? subscriptionId = null)
    {
        var root = string.IsNullOrWhiteSpace(_opt.WorkRootDir)
            ? Path.Combine(Path.GetTempPath(), "agentichub")
            : _opt.WorkRootDir!;
        Directory.CreateDirectory(root);

        // Resolve the Azure region: explicit user choice wins, then config
        // default, finally a hardcoded safe default. The string is
        // lower-cased and trimmed because azd is case-sensitive about
        // AZURE_LOCATION values.
        var location = (azureLocation ?? _opt.DefaultAzureLocation ?? "eastus")
            .Trim().ToLowerInvariant();

        // Sanitise the optional monorepo sub-path: trim, strip leading
        // slashes, reject absolute paths and any '..' traversal so a
        // catalog entry can never escape the cloned working directory.
        // A null/empty samplePath means "use repo root" (legacy behaviour).
        string? cleanSubPath = null;
        if (!string.IsNullOrWhiteSpace(samplePath))
        {
            var t = samplePath.Trim().Replace('\\', '/').Trim('/');
            if (t.Length > 0
                && !Path.IsPathRooted(t)
                && !t.Split('/').Any(seg => seg == ".." || seg == "."))
            {
                cleanSubPath = t;
            }
        }

        var s = new DeploymentSession
        {
            RepoUrl = repoUrl,
            WorkDir = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]),
            AzureLocation = location,
            SamplePath = cleanSubPath,
            TenantId = NormalizeGuid(tenantId),
            SubscriptionId = NormalizeGuid(subscriptionId)
        };
        _sessions[s.Id] = s;
        _store.SaveLater(s);
        _ = Task.Run(() => RunAsync(s));
        return s.Id;
    }

    public void Approve(string id)
    {
        if (_sessions.TryGetValue(id, out var s)) s.ApprovalTcs.TrySetResult();
    }

    public void Cancel(string id)
    {
        if (_sessions.TryGetValue(id, out var s))
        {
            s.Cts.Cancel();
            s.ApprovalTcs.TrySetCanceled();

            // Also reap the sandbox sibling. Cancelling the CTS unblocks
            // every 'await' in RunAsync but it does NOT kill the child
            // container cleanly — 'docker run --rm' only removes the
            // container AFTER the process inside exits, and a stuck
            // 'docker buildx build' child of a hung 'azd deploy' can keep
            // the sandbox alive indefinitely. Without this reap every
            // user-cancel leaks a zombie 'agentichub/sandbox:*' that
            // keeps holding buildx locks and misleading the user that
            // the deploy is still running. We do it fire-and-forget so
            // the UI cancel button stays responsive.
            _ = Task.Run(async () =>
            {
                try { await CleanOrphanSandboxesAsync($"cancel session {id}"); }
                catch (Exception ex) { _log.LogWarning(ex, "Post-cancel sandbox reap failed"); }
            });
        }
    }

    private async Task RunAsync(DeploymentSession s)
    {
        // No wall-clock limit on a session. A full 'azd up' for a rich
        // template can legitimately run well over an hour; enforcing a
        // timeout here would kill deploys mid-provisioning and leave Azure
        // resources in a dangling state. The three natural terminators
        // remain in place:
        //   • user click on Cancel -> s.Cts.Cancel()
        //   • DeploymentDoctor returning kind="give_up"
        //   • a plan step's own per-step timeout firing
        var ct = s.Cts.Token;

        // Hoisted ABOVE the try so the outer OperationCanceledException
        // handler can read which step was running when a timeout fired.
        // Without this the user only saw a generic "a step timed out"
        // with no indication of WHICH step.
        DeploymentStep? currentStep = null;
        DateTime? currentStepStartedAt = null;
        TimeSpan currentStepBudget = TimeSpan.Zero;

        // Long-lived sandbox container for this deploy. Created right after
        // workspace staging, torn down in the outer finally. Owning it at
        // this scope (instead of inline-using) keeps it alive across the
        // step loop, the hollow-deploy autonomous recovery, and any future
        // post-loop diagnostics, while still guaranteeing cleanup on every
        // exit path (success, failure, cancel, exception).
        SandboxSession? session = null;

        try
        {
            using var scope = _sp.CreateScope();
            var planner = scope.ServiceProvider.GetRequiredService<PlanExtractorAgent>();
            var verifier = scope.ServiceProvider.GetRequiredService<VerifierAgent>();

            // ---- Preflight: verify Docker daemon is reachable ----
            // The entire pipeline (agent sandbox, plan execution, Doctor
            // remediation) runs through 'docker run' against the host
            // daemon. Without it every phase fails in a confusing cascade:
            // runner exits 1 with no stdout, the fallback planner emits a
            // nonsense plan, the first step fails with the SAME docker
            // error, the Doctor is invoked, the Doctor ALSO needs docker,
            // crashes, and the user is left with three error messages and
            // no idea that the root cause is just "Docker Desktop not
            // running". Catch it ONCE, up front, with an actionable
            // message.
            var dockerError = await PreflightDockerAsync(ct);
            if (dockerError is not null)
            {
                s.ErrorMessage =
                    "Docker Desktop is not running or not reachable from this process.\n\n" +
                    "AgentStationHub spawns its deployment sandboxes as sibling containers " +
                    "on the host Docker daemon, so Docker Desktop must be started BEFORE a " +
                    "deploy is launched. Please:\n" +
                    "  1. Start Docker Desktop and wait for the whale icon to stop animating.\n" +
                    "  2. Retry the deploy.\n\n" +
                    $"Underlying error: {dockerError}";
                await Log(s, "err", s.ErrorMessage);
                await SetStatus(s, DeploymentStatus.Failed);
                return;
            }

            await SetStatus(s, DeploymentStatus.Cloning);
            await GitTool.CloneAsync(s.RepoUrl, s.WorkDir, msg => _ = Log(s, "info", msg), ct);

            // Resolve the directory the rest of the pipeline operates on.
            // For monorepo catalog entries that point to a sub-folder
            // (e.g. microsoft/agent-framework -> python/samples/05-end-to-end)
            // we move "downstream" of the clone: README, key files,
            // toolchain inspection, agent planning, the staged sandbox
            // workspace, and step execution all see the sub-folder as
            // their root. The clone itself stays at s.WorkDir so a
            // future feature (Doctor reading other parts of the repo,
            // submodule diagnostics) can still walk upward.
            var projectDir = s.WorkDir;
            if (!string.IsNullOrEmpty(s.SamplePath))
            {
                var candidate = Path.GetFullPath(
                    Path.Combine(s.WorkDir, s.SamplePath));
                var workRootFull = Path.GetFullPath(s.WorkDir);
                if (!candidate.StartsWith(workRootFull, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(candidate))
                {
                    s.ErrorMessage =
                        $"Configured sample path '{s.SamplePath}' does not exist " +
                        $"inside the cloned repo. Cannot continue.";
                    await Log(s, "err", s.ErrorMessage);
                    await SetStatus(s, DeploymentStatus.Failed);
                    return;
                }
                projectDir = candidate;
                await Log(s, "info",
                    $"Using monorepo sub-folder as project root: {s.SamplePath}");
            }

            await SetStatus(s, DeploymentStatus.Inspecting);
            var files = new FileTool(projectDir);
            // README is the primary source of deployment instructions. Read up
            // to 60 KB so we do not truncate large docs before the 'Deployment'
            // / 'Getting Started' section.
            var readme = files.ReadText("README.md", maxBytes: 60_000)
                          ?? files.ReadText("readme.md", maxBytes: 60_000)
                          ?? "";

            // Gather the content of key orchestration files so the planner can
            // decide whether 'azd' is viable or if we need different tooling
            // (docker compose, npm, python, make, terraform, ...).
            var keyFiles = new Dictionary<string, string?>
            {
                ["azure.yaml"]         = files.ReadText("azure.yaml")         ?? files.ReadText("azure.yml"),
                ["Dockerfile"]         = files.ReadText("Dockerfile"),
                ["docker-compose.yml"] = files.ReadText("docker-compose.yml") ?? files.ReadText("docker-compose.yaml") ?? files.ReadText("compose.yaml"),
                ["package.json"]       = files.ReadText("package.json", maxBytes: 4_000),
                ["pyproject.toml"]     = files.ReadText("pyproject.toml", maxBytes: 4_000),
                ["requirements.txt"]   = files.ReadText("requirements.txt", maxBytes: 4_000),
                ["Makefile"]           = files.ReadText("Makefile", maxBytes: 4_000),
                ["setup.sh"]           = files.ReadText("setup.sh", maxBytes: 4_000),
                ["main.bicep"]         = files.ReadText("infra/main.bicep", maxBytes: 4_000),
                ["main.tf"]            = files.ReadText("infra/main.tf", maxBytes: 4_000)
            };
            var presentFiles = keyFiles.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                                       .ToDictionary(kv => kv.Key, kv => kv.Value!);

            var infra = files.ListFiles("infra", "*.*")
                             .Concat(files.ListFiles(".", "azure.yaml"))
                             .Concat(files.ListFiles(".", "azure.yml"))
                             .Distinct()
                             .ToList();
            await Log(s, "info",
                $"README: {readme.Length} chars, infra files: {infra.Count}, " +
                $"key files present: [{string.Join(", ", presentFiles.Keys)}]");

            var toolchain = RepoInspector.Inspect(projectDir);
            var detected = string.Join(", ", toolchain.Summary());
            await Log(s, "status",
                $"Detected toolchains: {(string.IsNullOrEmpty(detected) ? "none" : detected)}");
            foreach (var r in toolchain.Rationale)
                await Log(s, "info", $"  • {r}");

            await SetStatus(s, DeploymentStatus.Planning);

            // Resolve the sandbox image up-front so the SAME image is used by
            // both the planning-phase Agent Framework runner and the execute-
            // phase step containers. On arm64 hosts this swaps the amd64-only
            // default image for our locally-built 'agentichub/sandbox:vN'.
            var imageToUse = await SandboxImageBuilder.ResolveAsync(
                _opt.SandboxImage,
                line => _ = Log(s, "info", line),
                ct);

            // Preferred path: invoke the Microsoft Agent Framework multi-agent
            // team running inside the Docker sandbox (Scout → TechClassifier →
            // DeploymentStrategist → SecurityReviewer). If that fails (runner
            // publish error, container issue, LLM error), fall back to the
            // legacy in-process PlanExtractorAgent so a deploy attempt is
            // never dead-in-the-water.
            DeploymentPlan plan;
            var runnerHost = _sp.GetService<SandboxRunnerHost>();
            if (runnerHost is not null)
            {
                try
                {
                    // Pull cross-session insights for this repo so the
                    // Strategist starts with prior learnings instead of
                    // re-discovering the same constraints from scratch.
                    var priorInsights = _memory.GetRelevantInsights(s.RepoUrl);
                    if (priorInsights.Count > 0)
                        await Log(s, "info",
                            $"Memory: passing {priorInsights.Count} prior insight(s) to the planning team.");

                    await Log(s, "status",
                        $"Invoking Agent Framework team in sandbox (region: {s.AzureLocation})...");
                    plan = await runnerHost.ExtractPlanAsync(
                        imageToUse, s.RepoUrl, projectDir, s.AzureLocation,
                        (lvl, line) => _ = Log(s, lvl, line),
                        ct,
                        priorInsights);
                }
                catch (Exception ex)
                {
                    await Log(s, "err",
                        $"Sandbox Agent Framework run failed: {ex.Message}. " +
                        "Falling back to in-process planner.");
                    plan = await planner.ExtractAsync(
                        s.RepoUrl, readme, infra, presentFiles, toolchain, ct);
                }
            }
            else
            {
                plan = await planner.ExtractAsync(
                    s.RepoUrl, readme, infra, presentFiles, toolchain, ct);
            }
            var (ok, reason) = PlanValidator.Validate(plan);
            if (!ok)
            {
                s.ErrorMessage = reason;
                await SetStatus(s, DeploymentStatus.Rejected);
                return;
            }

            // Guarantee a FRESH resource group on every deploy.
            // azd derives the Azure Resource Group name as 'rg-<envName>',
            // so reusing the same envName across deploys makes the second
            // `azd up` fight with:
            //   � the previous RG still in "Deleting" state (cannot
            //     create until it's gone � can take 5-10 min),
            //   � stale soft-deleted Key Vault / Cognitive Services
            //     accounts with identical names,
            //   � leftover Container Apps whose revisions block
            //     provisioning.
            // We append a short timestamp suffix to whatever name the
            // Strategist picked so every deploy lands in a brand-new RG.
            // The original prefix is preserved so users can still tell
            // at a glance which repo the RG belongs to.
            plan = EnforceUniqueAzdEnvName(plan,
                line => _ = Log(s, "info", line));

            s.Plan = plan;
            _store.SaveLater(s);
            await _hub.Clients.Group(s.Id).SendAsync("PlanReady", plan, ct);

            // Classification gate: the repo may be a course / library /
            // docs site / cli tool with no deploy target. The TechClassifier
            // inside the sandbox team flags these by returning a plan with
            // IsDeployable=false and a user-facing reason. Short-circuit
            // the pipeline here with a dedicated NotDeployable status.
            if (!plan.IsDeployable)
            {
                s.ErrorMessage = plan.NotDeployableReason
                    ?? "This repository does not appear to contain a deployable application.";
                await Log(s, "info",
                    $"Classifier verdict: not deployable ({plan.RepoKind ?? "unknown"}). {s.ErrorMessage}");
                await SetStatus(s, DeploymentStatus.NotDeployable);
                return;
            }

            // Surface which strategy the planner used (README vs inferred).
            var planSource = plan.VerifyHints.FirstOrDefault(h => h.StartsWith("source:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(planSource))
                await Log(s, "info", $"Plan {planSource}");

            if (!_opt.AutoApprove)
            {
                await SetStatus(s, DeploymentStatus.AwaitingApproval);
                await s.ApprovalTcs.Task.WaitAsync(ct);
            }

            await SetStatus(s, DeploymentStatus.Executing);

            // Ensure the sandbox has its own (Linux-native) Azure login cached
            // in a persistent Docker volume. First deploy triggers a device
            // code flow; subsequent deploys reuse the cached tokens.
            //
            // Tenant / subscription resolution priority:
            //   1. Explicit user choice from the Hub UI (s.TenantId /
            //      s.SubscriptionId) � pins the device-code login from
            //      the very first deploy, eliminates the "wrong default
            //      tenant" loop where a user with multiple tenants ends
            //      up authenticated against one that has no subscription
            //      and has to manually 'az logout' inside the sandbox.
            //   2. Host's azureProfile.json default subscription (only
            //      meaningful when the app is running on a developer
            //      laptop with az CLI installed).
            //   3. null � let device-code login pick the user's default.
            var hostTenantId = s.TenantId ?? ReadHostTenantId();
            await SandboxAzureAuth.EnsureAsync(
                imageToUse,
                hostTenantId,
                s.SubscriptionId,
                (lvl, line) => _ = Log(s, lvl, line),
                ct);

            // Stage the cloned repo into a per-session DOCKER NAMED VOLUME
            // and use it as /workspace for every plan step. This is the
            // definitive fix for the EACCES / exit 126 class of failures
            // we used to see on samples like azure-ai-travel-agents:
            // postinstall scripts (esbuild / tree-sitter / node-gyp-build)
            // produce binaries inside node_modules/.bin that the kernel
            // refuses to execute when /workspace is a virtio-fs / 9p
            // bind from a Windows host (lost +x bit, sometimes noexec).
            // A named volume lives on the Docker VM's native ext4 and
            // honours full Linux semantics. See SandboxWorkspaceVolume.
            string? workspaceVolume = null;
            try
            {
                workspaceVolume = await SandboxWorkspaceVolume.EnsureAsync(
                    s.Id, projectDir,
                    (lvl, line) => _ = Log(s, lvl, line),
                    ct);
            }
            catch (Exception ex)
            {
                await Log(s, "warn",
                    $"Could not create per-session workspace volume " +
                    $"({ex.GetType().Name}: {ex.Message}). Falling back to host bind " +
                    "mount � deploys that postinstall native binaries (esbuild, " +
                    "tree-sitter, node-gyp) may fail with EACCES on Windows hosts.");
            }

            // Generic preflight: walk every docker-compose*.y?ml under
            // /workspace and `touch` any env_file references that don't
            // exist yet. Many Azure samples (notably azure-ai-travel-
            // agents) ship hooks that build images via `docker compose
            // build`; compose v2 fails fast with exit 14 if a service
            // declares an env_file that is not on disk, even when the
            // file would have been irrelevant at build time. Hook scripts
            // try to seed these from .env.sample but routinely skip
            // services that don't have one, leaving the deploy to wedge
            // the Doctor in an 8-attempt loop chasing the same
            // ENOENT-after-ENOENT pattern. Pre-touching the placeholders
            // makes compose accept them; real runtime configuration is
            // still injected via `environment:` and Azure-set env vars.
            // Best-effort, idempotent, never fails the deploy on its own.
            if (workspaceVolume is not null)
            {
                await WorkspaceEnvFilePrimer.PrimeAsync(
                    workspaceVolume,
                    (lvl, line) => _ = Log(s, lvl, line),
                    ct);
            }

            // Boot the long-lived sandbox container. Every step from here on
            // is a `docker exec` into THIS container, so filesystem state,
            // env vars, MSAL tokens, ACR credentials, npm/pip caches and
            // any +x bit set by postinstall scripts persist across steps
            // for free. See SandboxSession class doc for the full rationale.
            session = await SandboxSession.StartAsync(
                s.Id, imageToUse, workspaceVolume, projectDir,
                (lvl, line) => _ = Log(s, lvl, line),
                ct);

            var docker = new DockerShellTool(session,
                (lvl, line) => _ = Log(s, lvl, line));
            var env = plan.Environment.ToDictionary(kv => kv.Key, kv => kv.Value);

            // Typed-action context: typed actions read deploy targets
            // (resource group, ACR name, per-service last-built image)
            // through this object instead of asking the LLM to extract
            // them via shell pipelines. We pass `env` BY REFERENCE so
            // every existing AzdEnvLoader.LoadAndMergeAsync(env, ...)
            // call automatically updates DeployContext.Env, and the
            // explicit MergeFromAzdEnv() calls below promote well-known
            // keys into typed properties (AcrName, ResourceGroup, etc).
            var deployCtx = new Services.Actions.DeployContext(
                sessionId: s.Id,
                repoUrl: s.RepoUrl,
                workDir: projectDir,
                azureLocation: s.AzureLocation,
                initialEnv: env);
            deployCtx.MergeFromAzdEnv(env);

            // NOTE: we deliberately DO NOT forward AZURE_SUBSCRIPTION_ID /
            // AZURE_TENANT_ID from the host's azureProfile.json here. The
            // sandbox has its own 'az login' (persistent Docker volume, see
            // SandboxAzureAuth) that may be tied to a DIFFERENT identity
            // than the one cached on the host. Forcing the host's sub id
            // into the container caused 'failed to resolve user access to
            // subscription' errors whenever the two accounts differed.
            // Instead we let azd discover the subscription via the sandbox
            // user's default ('az account show' inside the container).

            // Extract the azd environment name from the plan so we can tag
            // deployment-progress polling against it. Priority:
            //   1. env["AZURE_ENV_NAME"] if the planner set it explicitly;
            //   2. the first argument of 'azd env new <name>' in any step;
            //   3. a fallback derived from the session id.
            var azdEnvName = ResolveAzdEnvName(env, plan);

            // CRITICAL: export AZURE_ENV_NAME to the sandbox env so that
            // EVERY 'azd' subcommand (azd init, azd ai agent init, azd
            // env set, azd up, ...) sees a pre-resolved env name and
            // never falls back to the interactive "Enter a unique
            // environment name" prompt � which, in our headless DooD
            // pipeline, reads empty strings in a tight loop until the
            // silence cap fires (~200 reps observed). Setting this once
            // at the env-dictionary level is strictly more robust than
            // patching individual command lines because azd reads the
            // var across ALL its subcommands, not just `init`.
            if (!string.IsNullOrWhiteSpace(azdEnvName)
                && !env.ContainsKey("AZURE_ENV_NAME"))
            {
                env["AZURE_ENV_NAME"] = azdEnvName!;
            }

            // Mutable list so the Doctor can insert/replace steps at runtime.
            var steps = plan.Steps.ToList();
            var previousAttempts = new List<string>();
            // Dedicated counter for real LLM Doctor invocations (excludes
            // orchestrator-level cheap retries like ContainerAppValidation
            // Timeout). Enforced against DeploymentOptions.MaxDoctorInvocations
            // PerSession so a hallucinating model cannot loop on micro-
            // variations the near-duplicate guard fails to catch.
            int doctorInvocations = 0;

            // Empty-output dead-sandbox detector: when consecutive steps
            // exit 0 with zero captured output, the sandbox container has
            // almost certainly died and RunAsync is silently no-op'ing.
            // We bail out with an explicit error rather than letting the
            // deploy "succeed" with no real work done.
            int consecutiveEmptySuccesses = 0;

            // 8.3: Track which Doctor fix is currently "on probation".
            // When the Doctor proposes a remediation we record (errSig,
            // command); when the NEXT step in the loop succeeds we
            // persist that pair as a doctor.fix.{errSig} insight so the
            // next deploy of the same repo sees a proven fix and can try
            // it before speculating. Cleared on success (after persist)
            // or on subsequent fix application (overwritten).
            (string ErrSig, string Command)? pendingDoctorAttribution = null;

            // 8.8: Per-session set of error signatures for which we have
            // already prepended cross-session failed attempts to
            // previousAttempts. Avoids re-injecting on every Doctor pass.
            var injectedHistoricalSigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                currentStep = step;
                currentStepStartedAt = DateTime.UtcNow;

                // Pre-execution normalisation: rewrite pwsh invocations
                // that slip past the Strategist / Doctor. The sandbox
                // image has no PowerShell, so 'pwsh -c "..."' exits 127
                // with 'command not found'. Rather than bouncing through
                // a Doctor attempt, convert on the fly to 'bash -lc' —
                // the two shells share enough syntax for the patterns we
                // see in azd setup (command substitution, env assignment)
                // that a direct swap is correct in practice.
                if (ContainsPwsh(step.Command))
                {
                    var rewritten = RewritePwshToBash(step.Command);
                    await Log(s, "warn",
                        "Step references 'pwsh' which is not available in the sandbox. " +
                        "Auto-rewriting to 'bash -lc' to avoid a Doctor round-trip.",
                        step.Id);
                    step = step with { Command = rewritten };
                    steps[i] = step;
                }

                // Pre-execution normalisation #2: rewrite raw `azd up` to
                // the baked `agentic-azd-up` helper. On ARM hosts the
                // upstream `azd up` deploy phase invokes docker buildx
                // which delegates the per-service Dockerfile build to
                // qemu — for repos with a heavy Angular/webpack image
                // (e.g. azure-ai-travel-agents ui-angular) qemu hangs
                // indefinitely, tripping our 60-min SILENCE_TIMEOUT.
                // The baked helper splits provision (azd) + per-svc
                // build (`az acr build`, native amd64 in Azure) which
                // bypasses the qemu path entirely. The helper is a
                // strict drop-in replacement: same env, same cwd,
                // same idempotency guarantees.
                if (ContainsRawAzdUp(step.Command))
                {
                    var rewritten = RewriteAzdUpToBakedHelper(step.Command);
                    if (!string.Equals(rewritten, step.Command, StringComparison.Ordinal))
                    {
                        await Log(s, "warn",
                            "Step uses raw `azd up` — auto-rewriting to baked " +
                            "`agentic-azd-up` (split provision + per-svc " +
                            "`az acr build`) to bypass qemu hangs on ARM hosts. " +
                            $"original=`{Truncate(step.Command, 200)}` " +
                            $"rewritten=`{Truncate(rewritten, 200)}`",
                            step.Id);
                        step = step with { Command = rewritten };
                        steps[i] = step;
                    }
                }

                // Pre-execution normalisation #3: harden any 'azd init -t
                // <template>' against its two interactive prompts
                // ("Continue initializing an app in '/workspace'? (y/N)"
                // and "Enter a unique environment name"). Without this
                // we let the step fail once, fire the silence cap, and
                // burn a Doctor invocation � whereas the safe form is
                // a deterministic regex rewrite that any operator would
                // type by hand. Adds `-e <azdEnvName>` (using the env
                // name the orchestrator already resolved deterministically
                // from the plan) AND `--no-prompt`, then pipes `yes`
                // for older azd builds whose --no-prompt still asks for
                // env name. Idempotent: no-ops when the flags / yes
                // pipeline are already present.
                if (HardenAzdInit(step.Command, azdEnvName, out var hardened))
                {
                    await Log(s, "warn",
                        "Step uses 'azd init -t' � auto-hardening with " +
                        $"'-e {azdEnvName ?? "<envname>"} --no-prompt' and a " +
                        "'yes |' prefix to skip the directory-not-empty + " +
                        "env-name prompts that loop on stdin in headless mode. " +
                        $"rewritten=`{Truncate(hardened, 200)}`",
                        step.Id);
                    step = step with { Command = hardened };
                    steps[i] = step;
                }

                // Pre-execution normalisation #4: belt-and-braces inline
                // export of AZURE_ENV_NAME for ANY step that invokes azd.
                // We already export it via -e on `docker exec`, but we
                // observed it not propagating in some paths (and azd
                // reportedly clears it for some subcommands like
                // 'azd extension install' which then re-prompt
                // interactively for the env name in a tight stdin loop).
                // Inlining `export AZURE_ENV_NAME=<name>;` directly into
                // the bash one-liner is the most robust workaround:
                // it survives any shell-init quirks and is idempotent.
                if (!string.IsNullOrWhiteSpace(azdEnvName)
                    && IsAzdInvocation(step.Command)
                    && !ContainsAzureEnvNameExport(step.Command))
                {
                    var prefixed = PrefixAzureEnvNameExport(step.Command, azdEnvName!);
                    await Log(s, "info",
                        $"Inlining 'export AZURE_ENV_NAME={azdEnvName}' into " +
                        $"step {step.Id} so subcommands like 'azd extension " +
                        "install' don't re-prompt for env name.",
                        step.Id);
                    step = step with { Command = prefixed };
                    steps[i] = step;
                }

                // Pre-execution normalisation #5: replace `azd auth login`
                // with `az account show` because SandboxAzureAuth.EnsureAsync
                // ran BEFORE step 1 and already performed `az login
                // --use-device-code`. The sandbox image also runs
                // `azd config set auth.useAzCliAuth true` at build time,
                // which means `azd auth login` will hard-fail with:
                //   "current auth mode is 'az cli': 'azd auth login' is
                //    disabled when the auth mode is delegated"
                // Doctor then pivots to `az login --service-principal $X`
                // with empty env vars and burns a retry budget. The
                // deterministic fix is to recognise that the cached
                // identity is already authenticated and replace the
                // step with a cheap verification.
                if (IsAzdAuthLogin(step.Command))
                {
                    var verify = "bash -lc \"az account show -o table\"";
                    await Log(s, "info",
                        "Replacing 'azd auth login' with 'az account show' " +
                        "because SandboxAzureAuth already authenticated the " +
                        "sandbox via 'az login --use-device-code', and the " +
                        "image is configured for az-cli delegated auth (azd " +
                        "auth login would hard-fail in this mode).",
                        step.Id);
                    step = step with { Command = verify };
                    steps[i] = step;
                }

                await Log(s, "status", $"▶ Step {step.Id}: {step.Description}", step.Id);

                // Long-running azd commands can go silent for several minutes
                // while provisioning subscription-level deployments. Start a
                // background watcher that polls 'az deployment sub list' and
                // emits concise progress snapshots to the Live log.
                using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task? watcherTask = null;
                Task? heartbeatTask = null;
                if (IsLongRunningAzdCommand(step.Command) &&
                    !string.IsNullOrWhiteSpace(azdEnvName))
                {
                    var watcher = new DeploymentProgressWatcher(
                        imageToUse, azdEnvName!,
                        (lvl, line) => _ = Log(s, lvl, line, step.Id));
                    watcherTask = Task.Run(() => watcher.RunAsync(watcherCts.Token));
                }

                // Unconditional heartbeat for every long-running step (azd
                // AND 'az group delete' / 'purge'): every 60 seconds emit a
                // single status line with elapsed time. This reassures the
                // user that the sandbox is alive when the underlying
                // command produces no stdout for minutes at a time — the
                // classic case being 'az group delete' which is silent
                // from start to finish of a 20-minute teardown.
                if (IsLongRunningAzdCommand(step.Command))
                {
                    var stepStartedAt = DateTime.UtcNow;
                    var capturedStep = step;
                    heartbeatTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!watcherCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(60), watcherCts.Token);
                                var elapsed = DateTime.UtcNow - stepStartedAt;
                                await Log(s, "info",
                                    $"⏳ Step {capturedStep.Id} still running ({elapsed.TotalMinutes:0} min elapsed). " +
                                    "Sandbox is alive, waiting on the underlying command to return.",
                                    capturedStep.Id);
                            }
                        }
                        catch (OperationCanceledException) { /* normal on step completion */ }
                    });
                }

                DockerShellResult result;
                try
                {
                    // Pick a timeout that fits the kind of work: long-running
                    // azd commands (provision/deploy/up) legitimately run 30+
                    // minutes on rich templates, so applying the 10-minute
                    // generic default would kill the deploy mid-way.
                    var stepTimeout = step.Timeout
                        ?? (IsLongRunningAzdCommand(step.Command)
                            ? _opt.LongRunningStepTimeout
                            : _opt.DefaultStepTimeout);
                    currentStepBudget = stepTimeout;

                    // ── TYPED ACTION ROUTING ─────────────────────────
                    // If the step carries a typed action (LLM emitted
                    // {"action":{"type":"AcrBuild",...}} instead of a
                    // bash "cmd"), parse it and dispatch through the
                    // typed pipeline. This bypasses AzdEnvSubstitutor
                    // and the bash interpreter entirely � the action
                    // composes its argv from typed DeployContext fields
                    // (AcrName, ResourceGroup, LastBuiltImageRef) so
                    // there is no place for $(...) extraction or quote
                    // nesting to go wrong. See Services/Actions/.
                    var typedAction = !string.IsNullOrWhiteSpace(step.ActionJson)
                        ? Services.Actions.ActionRegistry.TryParse(step.ActionJson)
                        : null;
                    if (typedAction is not null)
                    {
                        // Pre-load azd env so DeployContext typed fields
                        // (AcrName, ResourceGroup, ...) reflect post-
                        // provision state. Same predicate as the bash
                        // path: only fire if anything looks azd-derived.
                        if (StepNeedsAzdEnv("$AZURE_", env))
                        {
                            await AzdEnvLoader.LoadAndMergeAsync(
                                docker, env,
                                (lvl, msg) => _ = Log(s, lvl, msg, step.Id),
                                ct);
                            deployCtx.MergeFromAzdEnv(env);
                        }

                        await Log(s, "info",
                            $"[typed-action] {typedAction.Type}: {typedAction.Describe(deployCtx)}",
                            step.Id);

                        var actionResult = await typedAction.ExecuteAsync(
                            deployCtx, docker, stepTimeout, ct);

                        result = new DockerShellResult(
                            actionResult.ExitCode, actionResult.TailLog ?? "")
                        {
                            TimedOutBySilence =
                                actionResult.Category == Services.Actions.ActionErrorCategory.BuildHang
                        };
                    }
                    else
                    {
                        // PRE-LOAD: if the step's command refers to any
                    // azd-derived variable (either via `azd env get-values`
                    // pipeline OR a direct $AZURE_*/$SERVICE_* reference)
                    // and that variable is NOT yet in `env`, run
                    // AzdEnvLoader once now. Without this pre-load,
                    // steps planned BEFORE the first azd-touching step
                    // (e.g. an out-of-order ACR remote build emitted by
                    // the Strategist as an early "optimization") would
                    // see an empty $REGISTRY and fail with the cryptic
                    // `argument --registry/-r: expected one argument`.
                    // The post-step loader (further down) still keeps
                    // env in sync after every azd state change. This
                    // pre-load is a best-effort no-op when there is
                    // nothing to load yet.
                    if (StepNeedsAzdEnv(step.Command, env))
                    {
                        await AzdEnvLoader.LoadAndMergeAsync(
                            docker, env,
                            (lvl, msg) => _ = Log(s, lvl, msg, step.Id),
                            ct);
                        deployCtx.MergeFromAzdEnv(env);
                    }

                    // Deterministic preprocessing: rewrite any
                    // `$(azd env get-values | ... | grep AZURE_X | ...)` and
                    // backtick equivalents into "$AZURE_X" using values
                    // already merged into `env` by AzdEnvLoader. The
                    // Doctor and Strategist routinely emit this pattern
                    // from a service subdir cwd where azd returns nothing,
                    // burning their entire 8-attempt remediation budget
                    // on shape-of-pipeline variations (`sed` → `grep|cut`
                    // → `awk`) of an extraction we already did once at
                    // /workspace. With the substitution applied the LLM's
                    // intent is preserved while the cwd-fragile lookup is
                    // gone. See AzdEnvSubstitutor for the regex contract.
                    var commandToRun = AzdEnvSubstitutor.Rewrite(
                        step.Command, env,
                        line => _ = Log(s, "info", line, step.Id));

                    // Heavy step heuristic: azd up / azd deploy / docker
                    // push / agentic-azd-up etc serialise dozens of
                    // multi-arch image builds + ACR pushes, where a
                    // single push of a 1+ GB Python or Angular image can
                    // sit genuinely silent for 30+ min under qemu
                    // emulation. Match anywhere in the command string —
                    // commands often arrive as `bash -lc "agentic-azd-up"`
                    // or with leading `cd <dir> && ...` boilerplate.
                    var sbCmdLower = (commandToRun ?? string.Empty).ToLowerInvariant();
                    bool sbIsHeavy =
                        sbCmdLower.Contains("azd up") ||
                        sbCmdLower.Contains("azd deploy") ||
                        sbCmdLower.Contains("azd provision") ||
                        sbCmdLower.Contains("agentic-azd-up") ||
                        sbCmdLower.Contains("agentic-azd-deploy") ||
                        sbCmdLower.Contains("agentic-acr-build") ||
                        sbCmdLower.Contains("agentic-build") ||
                        sbCmdLower.Contains("docker push") ||
                        sbCmdLower.Contains("docker buildx") ||
                        sbCmdLower.Contains("docker build");
                    var stepSilence = sbIsHeavy
                        ? TimeSpan.FromMinutes(Math.Max(60, _opt.StepSilenceBudget.TotalMinutes))
                        : _opt.StepSilenceBudget;

                    // Fragile-wait override: `az resource wait --created`
                    // and `az group wait --created` legitimately produce
                    // ZERO output for their entire run. With the default
                    // 15-minute silence budget that is fine, but when
                    // the --name has been guessed wrong (a recurring
                    // failure mode for Bicep templates that suffix names
                    // with a uniqueString hash) the command sits silent
                    // for its full 60-minute Azure-side cap, wedging
                    // the entire deploy. Cap such commands at 4 minutes
                    // of silence — long enough for legitimate
                    // provisioning waits (ACR, Cosmos, KV typically
                    // settle in <2 min once ARM has the request) but
                    // short enough that a wrong-name wait can't burn
                    // an hour. Note: CommandSafetyGuard already blocks
                    // the obvious cases up-front; this is the secondary
                    // defence for dynamic / variable-substituted forms
                    // the regex cannot statically catch.
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            sbCmdLower,
                            @"\baz\s+(resource|group|deployment)\s+wait\b.*--(created|exists|deleted)"))
                    {
                        var fragileCap = TimeSpan.FromMinutes(4);
                        if (stepSilence > fragileCap) stepSilence = fragileCap;
                    }

                    result = await docker.RunAsync(
                        commandToRun, step.WorkingDirectory,
                        env, stepTimeout, ct,
                        silenceBudget: stepSilence);
                    }
                }
                finally
                {
                    watcherCts.Cancel();
                    if (watcherTask is not null)
                    {
                        try { await watcherTask; } catch { /* expected on cancel */ }
                    }
                    if (heartbeatTask is not null)
                    {
                        try { await heartbeatTask; } catch { /* expected on cancel */ }
                    }
                }

                // Always log the step's exit envelope BEFORE branching on
                // success/failure. Without this we had blind sessions where
                // 5 consecutive 'azd up' / 'azd deploy' steps reported
                // success silently in <1s — the sandbox had been killed
                // and RunAsync was returning empty results. A single line
                // per step exit (with byte count, silence-timeout flag and
                // duration) is enough to spot that pattern instantly.
                {
                    var tailBytes = result.TailLog?.Length ?? 0;
                    var silenceFlag = result.TimedOutBySilence ? " SILENCE_TIMEOUT" : string.Empty;
                    var levelExit = result.ExitCode == 0 ? "info" : "warn";
                    await Log(s, levelExit,
                        $"⏹ Step {step.Id} exit={result.ExitCode} " +
                        $"tail={tailBytes}B{silenceFlag}",
                        step.Id);
                    if (result.ExitCode == 0
                        && tailBytes < 4
                        && !result.TimedOutBySilence)
                    {
                        consecutiveEmptySuccesses++;
                        await Log(s, "info",
                            $"Step {step.Id} returned exit=0 with empty output " +
                            $"(consecutive empties: {consecutiveEmptySuccesses}). " +
                            "Many idempotent commands (azd env set, az config) " +
                            "legitimately produce no stdout — only treated as " +
                            "dead-sandbox after a probe.",
                            step.Id);
                        if (consecutiveEmptySuccesses >= 3)
                        {
                            // Probe: is the sandbox container actually alive?
                            // 'docker inspect -f {{.State.Running}} asb-<sid>'
                            // returns "true\n" (~5B) when alive. If the
                            // container was reaped we abort; otherwise we
                            // reset the counter — the empties were just a
                            // streak of legitimately silent commands.
                            string probeOut = string.Empty;
                            try
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo("docker",
                                    $"inspect -f {{{{.State.Running}}}} {session!.ContainerName}")
                                {
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    UseShellExecute = false
                                };
                                using var p = System.Diagnostics.Process.Start(psi)!;
                                probeOut = (await p.StandardOutput.ReadToEndAsync()).Trim();
                                await p.WaitForExitAsync();
                            }
                            catch (Exception ex)
                            {
                                probeOut = "probe-error:" + ex.GetType().Name;
                            }

                            if (!string.Equals(probeOut, "true", StringComparison.OrdinalIgnoreCase))
                            {
                                await Log(s, "err",
                                    $"DEAD SANDBOX CONFIRMED via docker inspect " +
                                    $"(probe='{probeOut}', expected 'true'). " +
                                    "Aborting deploy.",
                                    step.Id);
                                s.ErrorMessage =
                                    $"Dead-sandbox guard tripped at step #{step.Id} " +
                                    $"'{step.Description}' — container '{session!.ContainerName}' " +
                                    $"is no longer running (probe='{probeOut}').";
                                await SetStatus(s, DeploymentStatus.Failed);
                                return;
                            }
                            await Log(s, "info",
                                $"Sandbox alive-probe OK after {consecutiveEmptySuccesses} " +
                                "empties — these are legitimate silent successes. " +
                                "Resetting counter.",
                                step.Id);
                            consecutiveEmptySuccesses = 0;
                        }
                    }
                    else
                    {
                        consecutiveEmptySuccesses = 0;
                    }

                    // Heavy-step output guard. Commands like `azd up`,
                    // `azd deploy`, `azd provision`, `agentic-azd-up`,
                    // `agentic-azd-deploy`, `agentic-acr-build` cannot
                    // legitimately exit 0 with <400B of output — azd
                    // streams hundreds of lines for every provisioning
                    // run. If we see one of these claim success silently
                    // it almost certainly means the helper script is
                    // broken (e.g. 0-byte file from a heredoc bake
                    // failure) or that exec mis-routed stdout. Treat as
                    // hard failure so the Doctor / Verifier can react.
                    if (result.ExitCode == 0)
                    {
                        var cmdLower = (step.Command ?? string.Empty).ToLowerInvariant();
                        bool isHeavy =
                            cmdLower.Contains("azd up") ||
                            cmdLower.Contains("azd deploy") ||
                            cmdLower.Contains("azd provision") ||
                            cmdLower.Contains("agentic-azd-up") ||
                            cmdLower.Contains("agentic-azd-deploy") ||
                            cmdLower.Contains("agentic-acr-build") ||
                            cmdLower.Contains("agentic-build");
                        if (isHeavy && tailBytes < 400)
                        {
                            // Whitelist: the agentic-* helpers legitimately
                            // short-circuit when the work is already done
                            // (e.g. all container apps already have real
                            // images, ACR build cache hit, etc). In those
                            // cases the helper prints a 1-line ✓ marker
                            // and exits 0 — that is NOT a broken pipe.
                            var tailLower = (result.TailLog ?? string.Empty).ToLowerInvariant();
                            bool fastPathOk =
                                tailLower.Contains("skipping redundant") ||
                                tailLower.Contains("already have real images") ||
                                tailLower.Contains("nothing to do") ||
                                tailLower.Contains("✓ all ") ||
                                tailLower.Contains("✓ skipped") ||
                                tailLower.Contains("[agentic-") && tailLower.Contains("skip");
                            if (fastPathOk)
                            {
                                await Log(s, "info",
                                    $"Heavy-step output guard: short output ({tailBytes}B) " +
                                    "but recognized fast-path / idempotent skip marker. Accepting exit 0.",
                                    step.Id);
                            }
                            else
                            {
                                await Log(s, "err",
                                $"Heavy step '{step.Description}' (cmd starts with " +
                                $"'{cmdLower.Split(' ', 2)[0]}') exited 0 but produced " +
                                $"only {tailBytes}B of output. This is impossible for a " +
                                "real azd/acr-build run — the helper script is likely " +
                                "broken or its stdout was lost. Aborting.",
                                step.Id);
                            s.ErrorMessage =
                                $"Heavy-step output guard tripped at step #{step.Id} " +
                                $"'{step.Description}': {tailBytes}B output is too short " +
                                "for a real provisioning run.";
                            await SetStatus(s, DeploymentStatus.Failed);
                            return;
                            }
                        }
                    }
                }

                if (result.ExitCode == 0)
                {
                    // Post-step hook: when a step that just provisioned
                    // Azure infrastructure succeeds, eagerly log the
                    // sandbox's Docker CLI into the deployment's Azure
                    // Container Registry so any subsequent step that
                    // pushes images (`azd deploy`, `docker push`,
                    // `docker buildx … --push`) authenticates without
                    // hitting `denied: authentication required`.
                    //
                    // Why this is necessary: each plan step runs in a
                    // FRESH ephemeral sandbox container. We mount a
                    // persistent named volume at /root/.docker (see
                    // SandboxAzureAuth.DockerConfigVolumeName) so the
                    // result of `az acr login` survives across steps,
                    // but SOMETHING has to actually run that login. The
                    // first step that creates the ACR is `azd up` /
                    // `azd provision`; everything downstream assumes the
                    // login is already in place. So immediately after
                    // either of those two commands succeeds we resolve
                    // the ACR endpoint from `azd env get-values` and
                    // run `az acr login --name <acrname>` once. The
                    // login is idempotent and cheap (~2 s) so re-running
                    // it after a follow-up `azd provision` is harmless.
                    //
                    // This is the definitive fix for the multi-service
                    // sample case (e.g. azure-ai-travel-agents) where
                    // `azd up` provisioned the ACR + Container Apps but
                    // the deploy phase silently failed at `docker push`,
                    // leaving the apps wired to the
                    // `containerapps-helloworld` placeholder image.
                    if (TouchesAzdProvision(step.Command)
                        && !string.IsNullOrWhiteSpace(azdEnvName))
                    {
                        await TryEnsureAcrLoginAsync(
                            s, docker, step, env, azdEnvName!, ct);
                    }

                    // After ANY azd-touching step refresh the azd
                    // environment values into our `env` dictionary so
                    // subsequent steps inherit them as ordinary env vars.
                    // This eliminates the brittle `$(azd env get-values
                    // | grep ... | cut ... | tr ...)` shell pattern that
                    // both the planner and the Doctor sometimes emit:
                    // those substitutions silently produce empty strings
                    // when the step's cwd is a service subdirectory
                    // (azd env get-values requires the project root),
                    // leading to errors like
                    // `argument --registry/-r: expected one argument`.
                    // With the values pre-injected as env vars the
                    // recovery step just references $AZURE_CONTAINER_
                    // REGISTRY_NAME and works. See AzdEnvLoader for the
                    // full rationale.
                    if (TouchesAnyAzd(step.Command))
                    {
                        await AzdEnvLoader.LoadAndMergeAsync(
                            docker, env,
                            (lvl, line) => _ = Log(s, lvl, line, step.Id),
                            ct);
                        deployCtx.MergeFromAzdEnv(env);
                    }

                    // 8.3: a Doctor fix from the previous failure unblocked
                    // us — persist (errSig -> command) so the next session's
                    // Doctor sees the proven fix as a prior insight before
                    // speculating. Confidence 0.85: high enough to influence
                    // the prompt without being treated as canonical truth.
                    if (pendingDoctorAttribution is { } pa)
                    {
                        try
                        {
                            _memory.UpsertInsight(
                                s.RepoUrl,
                                $"doctor.fix.{pa.ErrSig}",
                                pa.Command,
                                confidence: 0.85);
                            await Log(s, "info",
                                $"Memory: persisted Doctor fix for signature " +
                                $"[{pa.ErrSig}] — next deploy of this repo will see " +
                                $"this command as a proven remediation.",
                                step.Id);
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex,
                                "Failed to persist doctor.fix insight (non-fatal).");
                        }
                        pendingDoctorAttribution = null;
                    }
                    continue; // step succeeded, next.
                }

                // ---------------- Step failed ----------------
                var stepTail = string.IsNullOrWhiteSpace(result.TailLog)
                    ? "(no output captured)"
                    : result.TailLog;

                // [Probe] short-circuit — read-only diagnostic steps
                // tagged in their description with the literal "[Probe] "
                // prefix do NOT consume the Doctor budget and do NOT
                // fail the deploy when they exit non-zero. Their output
                // is logged as a warning and the orchestrator proceeds
                // to the next step. This keeps the diagnose-vs-fix
                // distinction sharp: the Strategist + Doctor can ask
                // for inspection (ls, cat, docker ps, az resource list)
                // without spending an attempt.
                var isProbe = (step.Description ?? string.Empty)
                    .TrimStart()
                    .StartsWith("[Probe]", StringComparison.OrdinalIgnoreCase);
                if (isProbe)
                {
                    await Log(s, "warn",
                        $"[Probe] Step {step.Id} exited {result.ExitCode}; this is a read-only " +
                        "diagnostic step and does not consume the Doctor budget. Continuing.",
                        step.Id);
                    continue;
                }

                // When the silence watchdog fired we want the Doctor to
                // see the signature 'StepSilent' even if the underlying
                // cancel-triggered tail log doesn't mention it (cancel
                // comes from outside the child process). Prepend a
                // synthetic marker so SummariseErrorSignature picks it
                // up and the Doctor can apply the ACR-remote-build fix.
                if (result.TimedOutBySilence)
                {
                    stepTail = "⚠  Step produced no output for the silence budget " +
                               "— treating as a hang.\n" + stepTail;
                }

                // Cheap orchestrator-level auto-retry for TRANSIENT
                // failure classes. Invoking the Doctor for these takes
                // ~30 s of LLM latency and the remediation it returns is
                // always "re-run the same step" — so we short-circuit it
                // here and save both time and a model call.
                //
                // Current auto-retry class: SignalKilled (host OOM killer
                // caught 'az'/azd mid-execution — a race condition with
                // no persistent state to fix, just transient memory
                // pressure). We attempt up to 2 retries per step; the
                // Doctor is only invoked if the issue persists (at which
                // point the Doctor's prompt tells it to escalate with a
                // buildx-cache-prune prep step before the next retry).
                var cheapRetrySignature = SummariseErrorSignature(stepTail);
                var priorCheapRetries = previousAttempts
                    .Count(a => a.Contains($"step {step.Id} [{cheapRetrySignature}] AUTO_RETRY",
                                           StringComparison.Ordinal));
                if (cheapRetrySignature == "SignalKilled" && priorCheapRetries < 2)
                {
                    await Log(s, "warn",
                        $"Step {step.Id} failed with [SignalKilled] — the host OOM killer " +
                        "interrupted 'az'. This is almost always transient. Auto-retrying the " +
                        $"step in 10 s (attempt {priorCheapRetries + 1}/2, Doctor not yet invoked).",
                        step.Id);
                    previousAttempts.Add(
                        $"step {step.Id} [SignalKilled] AUTO_RETRY: orchestrator-level no-LLM retry");
                    try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                    catch (OperationCanceledException) { throw; }
                    i--; // re-execute the same step on next iteration
                    continue;
                }

                // Same auto-retry treatment for Azure Container Apps
                // "Validation timed out" errors. Bicep is idempotent:
                // re-running the deployment retries only the Container
                // Apps that actually failed (the ones that succeeded
                // are simply re-validated and left alone). The regional
                // ACA control plane gets saturated when 8 Container
                // Apps are created simultaneously and some slip past
                // the validation budget, so waiting 30-60 s before the
                // retry usually lets it recover. We allow up to 3
                // auto-retries here (seen real-world cases needing 2
                // on azure-ai-travel-agents); the Doctor takes over
                // only if all 3 fail, meaning the issue is not the
                // transient validation race.
                if (cheapRetrySignature == "ContainerAppValidationTimeout"
                    && priorCheapRetries < 3)
                {
                    await Log(s, "warn",
                        $"Step {step.Id} failed with [ContainerAppValidationTimeout] — Azure " +
                        "Container Apps control plane timed out validating some revisions. " +
                        "This is almost always transient; Bicep is idempotent and will retry " +
                        $"only the failed Container Apps. Auto-retrying the step in 45 s " +
                        $"(attempt {priorCheapRetries + 1}/3, Doctor not yet invoked).",
                        step.Id);
                    previousAttempts.Add(
                        $"step {step.Id} [ContainerAppValidationTimeout] AUTO_RETRY: " +
                        "orchestrator-level no-LLM retry of idempotent Bicep deployment");
                    try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
                    catch (OperationCanceledException) { throw; }
                    i--;
                    continue;
                }

                // Ask the DeploymentDoctor (agent team in sandbox) to propose
                // a fix. The loop runs unbounded: natural terminations are
                //   (a) the Doctor returning kind="give_up",
                //   (b) the session budget (MaxSessionDuration) elapsing,
                //   (c) the user clicking Cancel.
                // The Doctor receives the list of previousAttempts in its
                // prompt and is explicitly instructed to escalate to
                // "give_up" rather than repeat a failed remediation.
                if (runnerHost is not null)
                {
                    Remediation? fix = null;

                    // Short-circuit for environmental failures that the
                    // Doctor cannot fix from inside the sandbox. Invoking
                    // it in these cases wastes an LLM call and muddies the
                    // error with a fake 'give_up' reason. Surface the real
                    // root cause instead.
                    var envError = DetectEnvironmentalFailure(stepTail);
                    if (envError is not null)
                    {
                        await Log(s, "err",
                            $"Step {step.Id} failed due to an environmental issue that cannot be " +
                            $"remediated by the Doctor: {envError} " +
                            "Please resolve it on the host and retry the deploy.",
                            step.Id);
                        // Skip the Doctor call; fall through to the
                        // 'no fix available' error path below.
                    }
                    else
                    {
                        // Hard budget: abort cleanly when the Doctor has
                        // already been invoked MaxDoctorInvocationsPerSession
                        // times for this deploy. The Doctor's own give_up
                        // logic is still the primary terminator; this is a
                        // safety net for hallucination loops.
                        if (_opt.MaxDoctorInvocationsPerSession > 0
                            && doctorInvocations >= _opt.MaxDoctorInvocationsPerSession)
                        {
                            // Last-resort canonical fix BEFORE giving up:
                            // when the failure signature is the noexec
                            // bind-mount problem (Windows + Docker Desktop),
                            // apply the relocate-node_modules-to-/tmp fix
                            // ONCE outside the Doctor budget. The Doctor
                            // wasted N attempts on micro-variations of
                            // --ignore-scripts that only delay the failure;
                            // the canonical fix is fully deterministic and
                            // safe to apply unconditionally for this class.
                            var lastSignature = SummariseErrorSignature(stepTail);
                            if (string.Equals(lastSignature, "NoexecBindMount",
                                    StringComparison.Ordinal))
                            {
                                await Log(s, "warn",
                                    "Doctor budget exhausted on a [NoexecBindMount] failure. " +
                                    "Applying the canonical relocate-node_modules-to-/tmp " +
                                    "recipe once and retrying the failing step before " +
                                    "marking Failed. This is a deterministic fix the Doctor " +
                                    "kept skipping in favour of unsuccessful --ignore-scripts " +
                                    "patches.",
                                    step.Id);

                                // Use the relocate-node-modules helper baked into the
                                // sandbox image (see SandboxImageBuilder.cs Dockerfile,
                                // tag v16+). Single command, no nested quoting, no
                                // LLM-template fragility — same script the Strategist's
                                // preventive step calls.
                                var relocateCmd = "relocate-node-modules /workspace";

                                var relocate = await docker.RunAsync(
                                    relocateCmd, ".",
                                    env,
                                    TimeSpan.FromMinutes(2),
                                    ct,
                                    silenceBudget: _opt.StepSilenceBudget);

                                if (relocate.ExitCode == 0)
                                {
                                    await Log(s, "info",
                                        "Last-resort node_modules relocation completed. " +
                                        "Retrying the failing step ONCE before giving up.",
                                        step.Id);
                                    previousAttempts.Add(
                                        $"step {step.Id} [LastResortRelocate] orchestrator " +
                                        "applied canonical fix outside Doctor budget");
                                    i--; // re-execute the same step
                                    // Do NOT return; fall through to the
                                    // outer loop. Mark a flag so a second
                                    // budget-exhausted hit really does
                                    // give up.
                                    if (!previousAttempts.Any(a =>
                                            a.Contains("[LastResortRelocateRetried]")))
                                    {
                                        previousAttempts.Add(
                                            "[LastResortRelocateRetried] one-shot retry token");
                                        continue;
                                    }
                                }
                                else
                                {
                                    await Log(s, "err",
                                        $"Last-resort relocate failed with exit " +
                                        $"{relocate.ExitCode}. Falling through to Failed.",
                                        step.Id);
                                }
                            }

                            await Log(s, "err",
                                $"Doctor invocation budget exhausted " +
                                $"({doctorInvocations}/{_opt.MaxDoctorInvocationsPerSession}). " +
                                "The orchestrator is aborting to stop burning LLM calls and " +
                                "cloud quota on a problem the Doctor cannot resolve from " +
                                "inside the sandbox. Review the Live log, address the root " +
                                "cause, and retry the deploy.",
                                step.Id);
                            s.ErrorMessage =
                                $"Doctor budget exhausted after {doctorInvocations} remediation " +
                                $"attempts. Last step: #{step.Id} '{step.Description}'. " +
                                "Manual intervention required.";
                            await SetStatus(s, DeploymentStatus.Failed);
                            return;
                        }

                        await Log(s, "status",
                            $"Step {step.Id} failed (exit {result.ExitCode}). " +
                            $"Invoking DeploymentDoctor agent for remediation " +
                            $"(attempt #{previousAttempts.Count + 1})...",
                            step.Id);

                        // Pre-Doctor deterministic patch: 'azd init -t
                        // <template>' refuses to overwrite a non-empty
                        // working dir and aborts with exit 1 + the prompt
                        // "Continue initializing an app in '/workspace'?
                        // (y/N)". Even after wrapping with `yes` so that
                        // first prompt auto-confirms, azd init then
                        // prompts for "Enter a unique environment name"
                        // which reads from stdin again and on some
                        // pipelines we observed reading empty strings in
                        // a tight loop until the silence cap fires. The
                        // robust fix is to pass `-e <envname>` and
                        // `--no-prompt` to azd init so it never asks for
                        // the env name AND it accepts the existing dir
                        // without confirmation. We keep `yes |` as a
                        // belt-and-braces measure for older azd builds
                        // that still prompt despite --no-prompt.
                        // Match: command contains 'azd init' AND tail
                        // contains either prompt signature. The patched
                        // command pipes 'y' lines AND adds -e + --no-prompt.
                        // We only auto-patch ONCE per step (guard via
                        // previousAttempts).
                        var azdInitContinue =
                            (step.Command ?? "").Contains("azd init", StringComparison.OrdinalIgnoreCase)
                            && ((stepTail ?? "").Contains("Continue initializing an app",
                                                 StringComparison.OrdinalIgnoreCase)
                                || (stepTail ?? "").Contains("Enter a unique environment name",
                                                 StringComparison.OrdinalIgnoreCase))
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:azd-init-yes]"));
                        if (azdInitContinue)
                        {
                            // Strip an existing 'bash -lc "..."' wrapper if
                            // present so we don't end up with nested quotes.
                            var inner = (step.Command ?? "").Trim();
                            var bashLc = System.Text.RegularExpressions.Regex.Match(
                                inner,
                                @"^bash\s+-lc\s+""(.+)""\s*$",
                                System.Text.RegularExpressions.RegexOptions.Singleline);
                            if (bashLc.Success)
                                inner = bashLc.Groups[1].Value.Replace("\\\"", "\"");

                            // Inject -e <envname> if missing. azd init reads
                            // the env name from -e / --environment first
                            // before prompting, so this skips the prompt
                            // entirely.
                            var hasEnvFlag = System.Text.RegularExpressions.Regex.IsMatch(
                                inner, @"\b(-e|--environment)\s+\S+");
                            if (!hasEnvFlag && !string.IsNullOrWhiteSpace(azdEnvName))
                            {
                                // Insert right after 'azd init' so the flag
                                // applies to the right subcommand.
                                inner = System.Text.RegularExpressions.Regex.Replace(
                                    inner,
                                    @"\bazd\s+init\b",
                                    $"azd init -e {azdEnvName}",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            }

                            // Strip --no-prompt if present: in a non-empty
                            // dir azd auto-DECLINES the "Continue?" prompt
                            // and exits 1 with "confirmation declined".
                            // We need the prompt to actually fire so our
                            // 'yes |' pipe can answer 'y'.
                            if (System.Text.RegularExpressions.Regex.IsMatch(inner, @"\s--no-prompt\b"))
                            {
                                inner = System.Text.RegularExpressions.Regex.Replace(
                                    inner, @"\s+--no-prompt\b", "");
                            }

                            // Pipe `yes` so the (y/N) confirmation auto-confirms.
                            if (!inner.TrimStart().StartsWith("yes ", StringComparison.Ordinal)
                                && !inner.Contains("yes |", StringComparison.Ordinal))
                            {
                                inner = "yes | " + inner;
                            }
                            var patched = $"bash -lc \"{inner.Replace("\"", "\\\"")}\"";

                            previousAttempts.Add(
                                $"[AutoPatch:azd-init-yes] step {step.Id} azd-init prompted " +
                                "in non-empty dir / for env name; injected -e + stripped " +
                                "--no-prompt + wrapped with 'yes |'.");
                            await Log(s, "status",
                                $"Auto-patch: 'azd init' looped on interactive prompt. " +
                                $"Injecting '-e {azdEnvName ?? "<envname>"}', stripping " +
                                $"--no-prompt, and piping 'yes' so prompts auto-resolve. " +
                                $"Replacing step {step.Id} and re-running (skipping LLM Doctor).",
                                step.Id);
                            steps[i] = step with { Command = patched };
                            i--; // re-execute the patched step at the same index
                            continue;
                        }

                        // Pre-Doctor deterministic patch: FoundryIQ-style
                        // bug where a postprovision shell script greps the
                        // resource list for `kind=='AIServices'` (Azure AI
                        // Foundry Hub) but the same template's Bicep
                        // creates the Cognitive Services account with
                        // `kind: 'OpenAI'` (classic Azure OpenAI). The
                        // typical failure signature is one of:
                        //   - "Azure AI Foundry Hub not found in resource group"
                        //   - "FOUNDRY_HUB_NAME" empty
                        //   - postprovision hook exit code 1 right after
                        //     setup_openai_deployments.sh
                        // The robust fix is a 1-line `sed -i` in /workspace
                        // that loosens the JMESPath filter so the existing
                        // OpenAI account is accepted. The model deployments
                        // are already created by the Bicep itself, so the
                        // script just needs to find the account by name.
                        // Marker prevents looping; we only patch once per
                        // step.
                        var foundryHubMissing =
                            !string.IsNullOrEmpty(stepTail)
                            && (stepTail.Contains("Azure AI Foundry Hub not found",
                                                  StringComparison.OrdinalIgnoreCase)
                                || stepTail.Contains("FOUNDRY_HUB_NAME",
                                                      StringComparison.OrdinalIgnoreCase))
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:foundry-hub-kind]"));
                        if (foundryHubMissing)
                        {
                            // Sandbox-side patch: scan every *.sh under
                            // /workspace for the offending JMESPath and
                            // broaden it to accept `OpenAI` too. We use
                            // `find ... -exec sed -i ...` so the fix
                            // applies regardless of which script in the
                            // repo holds the bug. The substitution is
                            // idempotent (a second run finds nothing to
                            // change).
                            var patchCmd =
                                "bash -lc \"" +
                                "set -e; " +
                                "cd /workspace; " +
                                "find . -type f -name '*.sh' -print0 | " +
                                "xargs -0 -r sed -i " +
                                "  \\\"s/\\[?kind=='AIServices'\\]/[?kind=='AIServices' || kind=='OpenAI']/g\\\"; " +
                                "echo 'AutoPatch: relaxed kind=='\\''AIServices'\\'' filter to also accept '\\''OpenAI'\\'' in /workspace/**/*.sh'" +
                                "\"";
                            previousAttempts.Add(
                                "[AutoPatch:foundry-hub-kind] postprovision script " +
                                "filtered Cognitive Services accounts by kind=='AIServices' " +
                                "but template provisions kind='OpenAI'; broadened filter " +
                                "with sed in /workspace/**/*.sh.");
                            await Log(s, "status",
                                "Auto-patch: postprovision hook can't find an Azure AI " +
                                "Foundry Hub because the template creates a kind='OpenAI' " +
                                "account, not kind='AIServices'. Inserting a sandbox sed " +
                                "step to broaden the filter in /workspace/**/*.sh and " +
                                "retrying the failed step.",
                                step.Id);
                            // insert_before: keep the failing step, prepend
                            // the patch step so it runs first, then retry.
                            var patchStep = new DeploymentStep(
                                step.Id, // re-use id; we re-execute the same index
                                "AutoPatch: relax kind=='AIServices' filter in /workspace shell scripts",
                                patchCmd,
                                step.WorkingDirectory);
                            steps.Insert(i, patchStep);
                            // Do NOT decrement i: index now points at the
                            // newly-inserted patch step; the failing step
                            // is at i+1 and runs next.
                            continue;
                        }

                        // Pre-Doctor deterministic patch: shell scripts in
                        // the cloned repo lack the executable bit. Common
                        // signature: stepTail contains 'Permission denied'
                        // AND references a '.sh' path. azd postprovision
                        // hooks copy themselves to /tmp and shell-out to
                        // ./scripts/*.sh which then fails because the
                        // checked-in scripts were committed without +x
                        // (typical when authored on Windows). Doctor in
                        // earlier sessions had to "learn" this fix three
                        // times in a row -- making it deterministic saves
                        // 3 LLM round-trips. Apply chmod +x to every *.sh
                        // under /workspace; idempotent and cheap.
                        var permDeniedSh =
                            !string.IsNullOrEmpty(stepTail)
                            && stepTail.Contains("Permission denied",
                                                 StringComparison.OrdinalIgnoreCase)
                            && System.Text.RegularExpressions.Regex.IsMatch(
                                stepTail, @"\.sh\b",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:chmod-sh]"));
                        if (permDeniedSh)
                        {
                            var chmodCmd =
                                "bash -lc \"" +
                                "set -e; cd /workspace; " +
                                "find . -type f -name '*.sh' -exec chmod +x {} +; " +
                                "echo 'AutoPatch: chmod +x applied to all .sh under /workspace'" +
                                "\"";
                            previousAttempts.Add(
                                "[AutoPatch:chmod-sh] shell script(s) under /workspace " +
                                "lacked +x bit causing 'Permission denied'; ran chmod +x " +
                                "on every *.sh.");
                            await Log(s, "status",
                                "Auto-patch: 'Permission denied' on a .sh path -- " +
                                "checked-in scripts lack the executable bit. Inserting " +
                                "a chmod +x step over /workspace/**/*.sh and retrying " +
                                "the failed step.",
                                step.Id);
                            var chmodStep = new DeploymentStep(
                                step.Id,
                                "AutoPatch: chmod +x for all *.sh under /workspace",
                                chmodCmd,
                                step.WorkingDirectory);
                            steps.Insert(i, chmodStep);
                            i--; // outer loop is for(...i++); land on inserted patch
                            continue;
                        }

                        // Pre-Doctor deterministic patch: Doctor proposed
                        // `az extension add --name X` but the extension
                        // does not exist in the index. Stops the Doctor
                        // from looping over candidate extension names
                        // (we observed it try 'ai-foundry' then
                        // 'azure-ai-foundry' then 'az upgrade --yes' then
                        // 'azure-ai-foundry' again). When the FAILING
                        // step is itself an `az extension add`, force a
                        // give_up so the orchestrator escalates to the
                        // resolver and ultimately to a source-repo fix.
                        var azExtAddFailed =
                            (step.Command ?? "").Contains("az extension add",
                                                          StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(stepTail)
                            && stepTail.Contains("No extension found with name",
                                                 StringComparison.OrdinalIgnoreCase)
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:az-ext-not-found]"));
                        if (azExtAddFailed)
                        {
                            previousAttempts.Add(
                                "[AutoPatch:az-ext-not-found] 'az extension add' " +
                                "for a non-existent extension; suppressing further " +
                                "Doctor extension-name guessing.");
                            await Log(s, "warn",
                                "Auto-patch: 'az extension add' targeted an extension " +
                                "that does not exist in the index. This typically " +
                                "means the Doctor mis-named the extension or the " +
                                "deploy plan references a hallucinated CLI plugin. " +
                                "Marking the step as a dead-end so the Doctor pivots " +
                                "instead of trying more extension names.",
                                step.Id);
                            // Replace the failing step with a no-op so
                            // the orchestrator can move on, while still
                            // logging a clear breadcrumb in the plan.
                            // We do NOT skip the step entirely (which
                            // would mask real errors) -- the no-op
                            // succeeds and we let the next step decide
                            // whether the missing extension actually
                            // matters.
                            var noopCmd =
                                "bash -lc \"echo 'skipped: az extension add (extension " +
                                "not in index); subsequent steps will surface any real " +
                                "missing-feature errors'\"";
                            steps[i] = step with
                            {
                                Command = noopCmd,
                                Description = step.Description +
                                    " [autopatched -> no-op: extension not in CLI index]"
                            };
                            i--; // re-execute (now-noop) step
                            continue;
                        }

                        // Pre-Doctor deterministic patch: Cosmos DB stuck
                        // in "failed provisioning state" + recurring
                        // "Please delete the previous instance" errors,
                        // optionally combined with the East US zonal-
                        // redundancy ServiceUnavailable signature. We
                        // observed Doctor loop 5+ attempts trying to:
                        //   1. azd env set USE_ZONE_REDUNDANCY false
                        //   2. az cosmosdb delete (succeeds but next
                        //      provision still races against a half-
                        //      deleted account)
                        //   3. azd env set AZURE_COSMOS_LOCATION eastus2
                        //      (Bicep ignores it on stuck deployment)
                        //   4. az cosmosdb wait (does NOT exist - the
                        //      cosmosdb command group has no 'wait')
                        //   5. az resource wait with hardcoded short
                        //      name (rejected by validator)
                        // Make this deterministic: list ALL cosmos
                        // accounts in the RG, delete each with --no-wait,
                        // then wait for actual deletion via
                        // `az resource wait --deleted` using the names
                        // resolved at runtime. Also force USE_ZONE_REDUNDANCY
                        // to false to dodge the eastus capacity issue.
                        // Distinguish two distinct Cosmos failure modes so
                        // each patch can fire ONCE independently:
                        //   - cosmos-zonal: ServiceUnavailable / zonal
                        //     redundant capacity error in eastus. Cosmos
                        //     accounts may not even exist yet (failed
                        //     during creation). Cleanup typically lists
                        //     0 accounts; we just set
                        //     USE_ZONE_REDUNDANCY=false and retry.
                        //   - cosmos-stuck: "DatabaseAccount X is in a
                        //     failed provisioning state. Please delete
                        //     the previous instance" — accounts now
                        //     exist as ARM resources stuck in failed
                        //     state. Real delete+wait needed.
                        // Without this split, the first signature
                        // consumed the single guard token and the
                        // patch refused to re-fire on the SECOND
                        // (different) signature, forcing the Doctor
                        // back into a loop.
                        var cosmosStuck =
                            !string.IsNullOrEmpty(stepTail)
                            && (stepTail.Contains("failed provisioning state",
                                                  StringComparison.OrdinalIgnoreCase)
                                || stepTail.Contains("Please delete the previous instance",
                                                  StringComparison.OrdinalIgnoreCase));
                        var cosmosZonal =
                            !string.IsNullOrEmpty(stepTail)
                            && stepTail.Contains("zonal redundant",
                                                 StringComparison.OrdinalIgnoreCase)
                            && (stepTail.Contains("Database account",
                                                  StringComparison.OrdinalIgnoreCase)
                                || stepTail.Contains("Cosmos",
                                                  StringComparison.OrdinalIgnoreCase));
                        var cosmosStuckCount = previousAttempts.Count(a =>
                            a.Contains("[AutoPatch:cosmos-stuck]"));
                        var cosmosZonalAlready = previousAttempts.Any(a =>
                            a.Contains("[AutoPatch:cosmos-zonal]"));
                        // cosmos-stuck cleanup is IDEMPOTENT (delete +
                        // wait + no-op when nothing matches) and we have
                        // observed cases where Bicep re-creates accounts
                        // in failed state on the next provision because
                        // the previous deletion didn't fully complete
                        // before the next attempt started. Allow it to
                        // refire up to 3 times before giving up to the
                        // Doctor (which is likely to suggest the same
                        // cleanup but with broken syntax like
                        // `azd env get --key`).
                        var cosmosFailedState =
                            (cosmosStuck && cosmosStuckCount < 3)
                            || (cosmosZonal && !cosmosZonalAlready);
                        if (cosmosFailedState)
                        {
                            // Resolve RG at runtime via azd, falling back
                            // to az group list. List cosmos accounts,
                            // fire delete --no-wait for each, then wait
                            // for each to be gone via az resource wait.
                            // Finally toggle USE_ZONE_REDUNDANCY=false so
                            // the next provision picks single-zone SKU.
                            //
                            // The sandbox already wraps every step in
                            // `bash -lc "<az auth bootstrap>; <ourCmd>"`,
                            // so any literal `bash -lc "<inner>"` we emit
                            // here goes through TWO levels of quoting.
                            // Nested $(...) and escaped \"...\" survive
                            // the first pass but the outer shell sees
                            // unbalanced quotes and breaks at the first
                            // `(`. To make the cleanup robust regardless
                            // of how many layers the runner adds, we
                            // base64-encode the actual script and pipe
                            // it through `base64 -d | bash` so the
                            // outer shells only ever see a single quoted
                            // string with NO special chars inside.
                            // Extract cosmos account name(s) from the
                            // error tail. Bicep deployments fail with
                            //   "DatabaseAccount cosmos-XYZ is in a
                            //    failed provisioning state ..."
                            // and `az cosmosdb list` does NOT return
                            // accounts that never finished initial
                            // provisioning — but `az resource list
                            // --resource-type Microsoft.DocumentDB/
                            // databaseAccounts` does. We collect names
                            // from BOTH sources plus the regex match
                            // on the error tail to be safe.
                            var failedCosmosNames = new System.Collections.Generic.HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);
                            try
                            {
                                var rxCosmos = new System.Text.RegularExpressions.Regex(
                                    @"DatabaseAccount\s+([A-Za-z0-9][A-Za-z0-9-]{2,49})\s+is\s+in\s+a\s+failed",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                foreach (System.Text.RegularExpressions.Match m in rxCosmos.Matches(stepTail ?? ""))
                                    if (m.Success) failedCosmosNames.Add(m.Groups[1].Value);
                            }
                            catch { /* regex failure is non-fatal */ }
                            // Also pick up names from earlier session
                            // logs in case the current step's tail
                            // doesn't include the original failure.
                            try
                            {
                                var rxCosmos2 = new System.Text.RegularExpressions.Regex(
                                    @"\b(cosmos-[a-z0-9]{6,30})\b",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                foreach (var le in s.Logs.TakeLast(200))
                                {
                                    foreach (System.Text.RegularExpressions.Match m in rxCosmos2.Matches(le.Message ?? ""))
                                        if (m.Success) failedCosmosNames.Add(m.Groups[1].Value);
                                }
                            }
                            catch { /* non-fatal */ }
                            var inlinedFailedNames = string.Join(" ",
                                failedCosmosNames.Select(n => "'" + n.Replace("'", "") + "'"));

                            var cleanupScript = string.Join("\n", new[]
                            {
                                "set +e",
                                "RG=$(azd env get-value AZURE_RESOURCE_GROUP 2>/dev/null)",
                                "if [ -z \"$RG\" ]; then",
                                "  RG=$(az group list --query \"[?starts_with(name,'rg-')].name | [0]\" -o tsv)",
                                "fi",
                                "echo \"AutoPatch:cosmos-failed-state RG=$RG\"",
                                "if [ -z \"$RG\" ]; then echo 'no RG resolved; aborting cleanup'; exit 0; fi",
                                "# Disable zone redundancy upfront so the next provision picks a single-zone SKU.",
                                "azd env set USE_ZONE_REDUNDANCY false || true",
                                "# Collect candidate cosmos account names from THREE sources:",
                                "#   (1) az cosmosdb list (succeeded accounts only)",
                                "#   (2) az resource list (sees ALL Microsoft.DocumentDB resources, including failed-state)",
                                "#   (3) names parsed from the error tail / log history (passed in via $FAILED_NAMES)",
                                "NAMES_A=$(az cosmosdb list -g \"$RG\" --query \"[].name\" -o tsv 2>/dev/null)",
                                "NAMES_B=$(az resource list -g \"$RG\" --resource-type Microsoft.DocumentDB/databaseAccounts --query \"[].name\" -o tsv 2>/dev/null)",
                                "FAILED_NAMES=" + (string.IsNullOrWhiteSpace(inlinedFailedNames) ? "''" : "\"" + string.Join(" ", failedCosmosNames) + "\""),
                                "ALL_NAMES=$(printf '%s\\n%s\\n%s\\n' \"$NAMES_A\" \"$NAMES_B\" \"$FAILED_NAMES\" | tr ' ' '\\n' | awk 'NF' | sort -u)",
                                "echo \"cosmos candidates in $RG (cosmosdb-list | resource-list | parsed): A=[$NAMES_A] B=[$NAMES_B] F=[$FAILED_NAMES]\"",
                                "if [ -z \"$ALL_NAMES\" ]; then echo 'no cosmos accounts found anywhere; AutoPatch is a no-op'; exit 0; fi",
                                "for n in $ALL_NAMES; do",
                                "  echo \"deleting $n via az resource delete (works on failed-state too)\"",
                                "  az resource delete -g \"$RG\" --resource-type Microsoft.DocumentDB/databaseAccounts --name \"$n\" --verbose 2>&1 | tail -n 5 || true",
                                "done",
                                "# Poll for actual deletion via az cosmosdb show + az resource show.",
                                "# `az resource wait --deleted` against Microsoft.DocumentDB hits a",
                                "# 'locations/operationsStatus' subresource whose api-version we",
                                "# don't have registered for this provider, surfacing a",
                                "# NoRegisteredProviderFound error and bailing out of the wait",
                                "# without actually waiting. Polling explicitly is reliable.",
                                "for n in $ALL_NAMES; do",
                                "  echo \"waiting for $n to be fully deleted (poll up to 15min)\"",
                                "  for try in $(seq 1 60); do",
                                "    if ! az cosmosdb show -g \"$RG\" -n \"$n\" >/dev/null 2>&1 \\",
                                "       && ! az resource show -g \"$RG\" --resource-type Microsoft.DocumentDB/databaseAccounts --name \"$n\" >/dev/null 2>&1; then",
                                "      echo \"  $n deletion confirmed (try=$try)\"",
                                "      break",
                                "    fi",
                                "    sleep 15",
                                "  done",
                                "done",
                                "# Final verification: list any cosmos accounts STILL present",
                                "# in the RG so the next provision attempt sees a clean slate",
                                "# (or so we know the cleanup is incomplete).",
                                "REMAINING=$(az resource list -g \"$RG\" --resource-type Microsoft.DocumentDB/databaseAccounts --query \"[].name\" -o tsv 2>/dev/null)",
                                "if [ -n \"$REMAINING\" ]; then",
                                "  echo \"WARNING: cosmos accounts still present after cleanup: $REMAINING\"",
                                "else",
                                "  echo \"verified: no cosmos accounts remain in $RG\"",
                                "fi",
                                "echo 'AutoPatch:cosmos-failed-state done'",
                                ""
                            });
                            var cleanupB64 = Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes(cleanupScript));
                            // Single-quoted base64 string is opaque to
                            // every layer of bash -lc wrapping above us.
                            var cosmosCleanupCmd =
                                "bash -lc 'echo " + cleanupB64 + " | base64 -d | bash'";
                            // Tag THIS specific signature so the OTHER
                            // signature can still re-fire its own pass.
                            // cosmos-stuck takes precedence (it implies
                            // accounts already exist and need delete);
                            // cosmos-zonal alone needs only the env-set.
                            var firedMarker = cosmosStuck
                                ? "[AutoPatch:cosmos-stuck]"
                                : "[AutoPatch:cosmos-zonal]";
                            previousAttempts.Add(
                                firedMarker + " Cosmos DB account(s) in failed " +
                                "provisioning state and/or zonal-redundancy capacity " +
                                "error; listed cosmos accounts in RG, deleted each " +
                                "via az cosmosdb delete --no-wait, awaited full " +
                                "deletion via az resource wait --deleted, and forced " +
                                "USE_ZONE_REDUNDANCY=false.");
                            await Log(s, "status",
                                "Auto-patch " + firedMarker + ": detected Cosmos DB " +
                                "failure. Inserting a deterministic cleanup step " +
                                "(list+delete+wait on all cosmos accounts in RG; " +
                                "disable zone redundancy) ahead of the failing step " +
                                "instead of letting the Doctor loop on invalid " +
                                "'az cosmosdb wait' commands.",
                                step.Id);
                            var cosmosStep = new DeploymentStep(
                                step.Id,
                                "AutoPatch: delete failed Cosmos DB accounts and wait for full deletion",
                                cosmosCleanupCmd,
                                step.WorkingDirectory,
                                TimeSpan.FromMinutes(20));
                            steps.Insert(i, cosmosStep);
                            // The outer loop is `for(...i++)`. After
                            // continue;, i advances to the original
                            // failing step (now at i+1) and skips our
                            // cleanup. Decrement so the next iteration
                            // lands on the inserted cleanup step.
                            i--;
                            continue;
                        }

                        // 8.7.x: [AutoPatch:azd-remote-build] — when azd
                        // deploy fails because the buildpacks `pack` CLI
                        // bundled in the sandbox image is too old to
                        // talk to the host docker daemon ("client
                        // version 1.38 is too old. Minimum supported
                        // API version is 1.40"), download and install
                        // the latest `pack` binary from GitHub releases
                        // into /usr/local/bin (which precedes the
                        // bundled location on PATH) and also set
                        // DOCKER_API_VERSION=1.40 in azd's env so any
                        // legacy docker client still in use negotiates
                        // a compatible API version.
                        //
                        // Note: azd's `docker: { remoteBuild: true }`
                        // is NOT a fix here because GPT-RAG's failing
                        // service has no Dockerfile (the build is via
                        // Oryx buildpacks — "Building Docker image
                        // from source"), and remoteBuild only kicks
                        // in when a Dockerfile is present.
                        var dockerApiTooOld =
                            !string.IsNullOrEmpty(stepTail)
                            && (stepTail.Contains("Minimum supported API version",
                                                  StringComparison.OrdinalIgnoreCase)
                                || (stepTail.Contains("client version",
                                                      StringComparison.OrdinalIgnoreCase)
                                    && stepTail.Contains("is too old",
                                                          StringComparison.OrdinalIgnoreCase)))
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:azd-remote-build]"));
                        if (dockerApiTooOld)
                        {
                            // Upgrade pack to a recent release
                            // (>= 0.34 negotiates docker API correctly)
                            // and set DOCKER_API_VERSION in azd env.
                            // Base64-wrap for the same nested-bash
                            // quoting reasons as the cosmos cleanup.
                            var upgradeScript = string.Join("\n", new[]
                            {
                                "set -e",
                                "PACK_VERSION=v0.36.4",
                                "ARCH=$(uname -m)",
                                "case \"$ARCH\" in",
                                "  x86_64) PACK_TGZ=pack-${PACK_VERSION}-linux.tgz ;;",
                                "  aarch64) PACK_TGZ=pack-${PACK_VERSION}-linux-arm64.tgz ;;",
                                "  *) echo \"unknown arch $ARCH; defaulting to linux\"; PACK_TGZ=pack-${PACK_VERSION}-linux.tgz ;;",
                                "esac",
                                "URL=\"https://github.com/buildpacks/pack/releases/download/${PACK_VERSION}/${PACK_TGZ}\"",
                                "echo \"AutoPatch:azd-remote-build downloading $URL\"",
                                "curl -fsSL -o /tmp/pack.tgz \"$URL\"",
                                "tar -xzf /tmp/pack.tgz -C /tmp",
                                "install -m 0755 /tmp/pack /usr/local/bin/pack",
                                "rm -f /tmp/pack /tmp/pack.tgz",
                                "echo \"new pack:\"; pack version || true",
                                "azd env set DOCKER_API_VERSION 1.40 || true",
                                "echo 'AutoPatch:azd-remote-build done'",
                                ""
                            });
                            var upgradeB64 = Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes(upgradeScript));
                            var upgradeCmd =
                                "bash -lc 'echo " + upgradeB64 + " | base64 -d | bash'";
                            previousAttempts.Add(
                                "[AutoPatch:azd-remote-build] azd deploy failed " +
                                "because the bundled `pack` CLI in the sandbox " +
                                "speaks only Docker API 1.38 while the host " +
                                "daemon requires >= 1.40. Installed pack v0.36.4 " +
                                "to /usr/local/bin and set DOCKER_API_VERSION=1.40 " +
                                "in the azd env so the next `azd deploy` uses a " +
                                "modern client that negotiates a supported API.");
                            await Log(s, "status",
                                "Auto-patch [AutoPatch:azd-remote-build]: bundled " +
                                "pack CLI too old (Docker API 1.38). Installing " +
                                "latest pack to /usr/local/bin and setting " +
                                "DOCKER_API_VERSION=1.40 in azd env before " +
                                "retrying the failing step.",
                                step.Id);
                            var upgradeStep = new DeploymentStep(
                                step.Id,
                                "AutoPatch: upgrade pack CLI for Docker API >= 1.40",
                                upgradeCmd,
                                step.WorkingDirectory,
                                TimeSpan.FromMinutes(3));
                            steps.Insert(i, upgradeStep);
                            i--;
                            continue;
                        }

                        // 8.7.y: [AutoPatch:embedding-quota] — when an
                        // ARM template validation fails with
                        //   InsufficientQuota: This operation require N
                        //   new capacity in quota Tokens Per Minute
                        //   (thousands) - text-embedding-3-large
                        // the Doctor's `sed` patches on main.parameters.json
                        // can only reduce capacity to 1; the sub already
                        // has 350/350 TPM consumed by leftover model
                        // deployments from previous failed deploys, so
                        // even capacity=1 fails. The deterministic fix
                        // is to free quota by deleting every
                        // text-embedding-3-large model deployment that
                        // lives OUTSIDE the current RG (i.e. orphans
                        // from prior sessions), then retry. We never
                        // touch deployments in the current RG: they
                        // either don't exist yet (validation phase) or
                        // belong to the in-flight deploy.
                        var embeddingQuota =
                            !string.IsNullOrEmpty(stepTail)
                            && stepTail.Contains("InsufficientQuota",
                                                 StringComparison.OrdinalIgnoreCase)
                            && stepTail.Contains("text-embedding",
                                                 StringComparison.OrdinalIgnoreCase)
                            && !previousAttempts.Any(a =>
                                a.Contains("[AutoPatch:embedding-quota]"));
                        if (embeddingQuota)
                        {
                            var quotaScript = string.Join("\n", new[]
                            {
                                "set +e",
                                "echo 'AutoPatch:embedding-quota starting'",
                                "RG_NOW=$(azd env get-value AZURE_RESOURCE_GROUP 2>/dev/null)",
                                "echo \"current RG=$RG_NOW\"",
                                "# Enumerate all OpenAI / AIServices accounts in the sub.",
                                "ACCS=$(az cognitiveservices account list -o tsv --query \"[?kind=='OpenAI' || kind=='AIServices'].[name,resourceGroup]\" 2>/dev/null)",
                                "if [ -z \"$ACCS\" ]; then echo 'no AOAI/AIServices accounts in sub'; exit 0; fi",
                                "FREED=0",
                                "echo \"$ACCS\" | while IFS=$'\\t' read -r ACC RG; do",
                                "  [ -z \"$ACC\" ] && continue",
                                "  # Skip accounts in the in-flight RG to avoid touching the current deploy.",
                                "  if [ -n \"$RG_NOW\" ] && [ \"$RG\" = \"$RG_NOW\" ]; then",
                                "    echo \"skip $ACC ($RG) — current RG\"; continue;",
                                "  fi",
                                "  DEPS=$(az cognitiveservices account deployment list -g \"$RG\" -n \"$ACC\" -o tsv --query \"[?properties.model.name=='text-embedding-3-large'].[name,sku.capacity]\" 2>/dev/null)",
                                "  [ -z \"$DEPS\" ] && continue",
                                "  echo \"$ACC ($RG) has text-embedding-3-large deployments:\"",
                                "  echo \"$DEPS\"",
                                "  echo \"$DEPS\" | while IFS=$'\\t' read -r DEP CAP; do",
                                "    [ -z \"$DEP\" ] && continue",
                                "    echo \"  deleting $DEP from $ACC ($RG) — frees $CAP TPM\"",
                                "    az cognitiveservices account deployment delete -g \"$RG\" -n \"$ACC\" --deployment-name \"$DEP\" 2>&1 | tail -n 3 || true",
                                "  done",
                                "done",
                                "# Show remaining usage so we can see if we freed enough.",
                                "LOC=$(azd env get-value AZURE_LOCATION 2>/dev/null)",
                                "[ -z \"$LOC\" ] && LOC=eastus",
                                "echo \"remaining usage in $LOC:\"",
                                "az cognitiveservices usage list --location \"$LOC\" --query \"[?contains(name.value,'OpenAI.Standard.text-embedding-3-large')].{quota:limit,used:currentValue}\" -o table 2>/dev/null || true",
                                "echo 'AutoPatch:embedding-quota done'",
                                ""
                            });
                            var quotaB64 = Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes(quotaScript));
                            var quotaCmd =
                                "bash -lc 'echo " + quotaB64 + " | base64 -d | bash'";
                            previousAttempts.Add(
                                "[AutoPatch:embedding-quota] ARM validation " +
                                "failed with InsufficientQuota for text-embedding-3-large " +
                                "(sub had 350/350 TPM consumed). Enumerated all " +
                                "OpenAI/AIServices accounts in the sub and deleted " +
                                "every text-embedding-3-large deployment outside " +
                                "the current resource group to free quota for this " +
                                "deploy. Did not touch the in-flight RG.");
                            await Log(s, "status",
                                "Auto-patch [AutoPatch:embedding-quota]: detected " +
                                "InsufficientQuota for text-embedding-3-large. " +
                                "Inserting a deterministic cleanup step that " +
                                "deletes every text-embedding-3-large deployment " +
                                "outside the current RG to free TPM quota, then " +
                                "retrying the failing step.",
                                step.Id);
                            var quotaStep = new DeploymentStep(
                                step.Id,
                                "AutoPatch: free text-embedding-3-large TPM quota by deleting orphan deployments",
                                quotaCmd,
                                step.WorkingDirectory,
                                TimeSpan.FromMinutes(10));
                            steps.Insert(i, quotaStep);
                            i--;
                            continue;
                        }

                        // 8.8: prefill previousAttempts with cross-session
                        // failed attempts for the same error signature on
                        // the same repo so the Doctor pivots instead of
                        // re-trying known dead-ends. Only inject once per
                        // signature per session (subsequent Doctor calls in
                        // the same session already see them in the list).
                        var preDoctorErrSig = SummariseErrorSignature(stepTail);
                        if (!string.IsNullOrEmpty(preDoctorErrSig)
                            && injectedHistoricalSigs.Add(preDoctorErrSig))
                        {
                            try
                            {
                                var historical = _memory.GetRelevantInsights(s.RepoUrl)
                                    .FirstOrDefault(insight =>
                                        string.Equals(insight.Key,
                                            $"doctor.giveup.{preDoctorErrSig}",
                                            StringComparison.OrdinalIgnoreCase));
                                if (historical is not null
                                    && !string.IsNullOrWhiteSpace(historical.Value))
                                {
                                    var lines = historical.Value.Split(
                                        '\n',
                                        StringSplitOptions.RemoveEmptyEntries
                                        | StringSplitOptions.TrimEntries);
                                    int injected = 0;
                                    foreach (var line in lines)
                                    {
                                        var marker = $"[prior-session FAILED] {Truncate(line, 180)}";
                                        if (!previousAttempts.Contains(marker, StringComparer.Ordinal))
                                        {
                                            previousAttempts.Insert(0, marker);
                                            injected++;
                                        }
                                    }
                                    if (injected > 0)
                                        await Log(s, "info",
                                            $"Memory: prepended {injected} prior-session " +
                                            $"failed attempt(s) for signature " +
                                            $"[{preDoctorErrSig}] to PREVIOUS_ATTEMPTS so the " +
                                            "Doctor avoids known dead-ends.",
                                            step.Id);
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogDebug(ex,
                                    "Failed to load doctor.giveup insights (non-fatal).");
                            }
                        }

                        try
                        {
                            doctorInvocations++;

                            // Doctor backend selection — strict, no
                            // silent fall-back. When the Foundry hosted
                            // Doctor is configured (Foundry:UseFoundryDoctor=true
                            // + DoctorAgentEndpoint set, registers the
                            // FoundryDoctorClient in DI), we use ONLY it.
                            // If it returns null we surface the failure so
                            // the user knows their hosted agent is broken
                            // instead of masking it with the in-sandbox
                            // Doctor. When the Foundry client is NOT
                            // registered, we use the in-sandbox Doctor as
                            // the sole backend (legacy default).
                            var foundryDoctor = _sp.GetService<FoundryDoctorClient>();
                            if (foundryDoctor is not null)
                            {
                                fix = await foundryDoctor.RemediateAsync(
                                    projectDir,
                                    plan with { Steps = steps.ToList() },
                                    step.Id, stepTail, previousAttempts,
                                    (lvl, line) => _ = Log(s, lvl, line, step.Id),
                                    ct,
                                    _memory.GetRelevantInsights(s.RepoUrl));

                                if (fix is null)
                                {
                                    await Log(s, "err",
                                        "[Foundry] hosted Doctor returned no " +
                                        "remediation (auth, HTTP, or agent " +
                                        "image-pull failure). Fall-back is " +
                                        "DISABLED — failing the step. Check " +
                                        "the agent status in the Foundry " +
                                        "portal or unset Foundry:UseFoundryDoctor " +
                                        "to use the in-sandbox Doctor.",
                                        step.Id);
                                }
                            }
                            else
                            {
                                fix = await runnerHost.RemediateAsync(
                                    imageToUse, projectDir,
                                    // Rebuild plan DTO from the current (possibly
                                    // already-remediated) step list so the Doctor
                                    // sees the real state.
                                    plan with { Steps = steps.ToList() },
                                    step.Id, stepTail, previousAttempts,
                                    (lvl, line) => _ = Log(s, lvl, line, step.Id),
                                    ct,
                                    // Same insights as the planner: the Doctor
                                    // benefits even more from learnings like
                                    // "doctor.lastGiveUp" or "lastSuccess.endpoint"
                                    // when iterating against the same repo.
                                    _memory.GetRelevantInsights(s.RepoUrl));
                            }
                        }
                        catch (Exception rex)
                        {
                            await Log(s, "err",
                                $"Doctor invocation threw: {rex.Message}. " +
                                "Falling through to failure.", step.Id);
                        }
                    }

                    if (fix is not null && fix.Kind != "give_up" && fix.NewSteps.Count > 0)
                    {
                        // Defensive guard: reject remediations that silently
                        // degrade the deploy to a hollow state. We've hit the
                        // "Succeeded but Container Apps stuck on hello-world
                        // placeholder" failure mode when the Doctor replaced
                        // 'azd up' with 'azd provision' alone, or set
                        // AZURE_SERVICE_*_RESOURCE_EXISTS=true to skip
                        // build+push+deploy. Both are explicitly forbidden
                        // by the Doctor prompt; this is the orchestrator's
                        // belt-and-braces enforcement. When detected we
                        // DROP the remediation and let the step fail again,
                        // eventually forcing the Doctor into a real fix or
                        // give_up.
                        bool DegradesDeploy(IEnumerable<DeploymentStep> newSteps)
                        {
                            // Remediation batches that include a REAL
                            // 'az acr build' + 'az containerapp update'
                            // are LEGITIMATE ACR-remote-build recoveries
                            // (Doctor's [StepSilent] canonical fix). These
                            // genuinely produce + activate the image, so
                            // the accompanying 'azd env set *_RESOURCE_
                            // EXISTS true' flag is an intentional nudge
                            // to stop the next 'azd deploy' from re-
                            // triggering the hang — not a skip. Detect
                            // the combo and skip the blanket reject.
                            bool hasAcrBuildAndUpdate =
                                newSteps.Any(s =>
                                    (s.Command ?? "").Contains("az acr build",
                                        StringComparison.OrdinalIgnoreCase))
                                && newSteps.Any(s =>
                                    (s.Command ?? "").Contains("az containerapp update",
                                        StringComparison.OrdinalIgnoreCase));

                            // Rule 1 is only meaningful when the step being
                            // REPLACED is itself an 'azd up' / 'azd deploy'
                            // step. If the failing step is a prerequisite
                            // (e.g. 'az acr create', 'az group create'),
                            // the Doctor is allowed to fold an 'azd provision'
                            // into the new step to materialise the RG / env
                            // BEFORE re-creating that prerequisite. Without
                            // this exception we wedge legitimate fixes like
                            // "RG var was empty because azd hadn't provisioned
                            // yet — run azd provision, query the real RG, then
                            // create ACR" (seen on Investment-Analysis-Sample
                            // Step 17, Apr 2026).
                            var failingCmdLc = (step.Command ?? string.Empty).ToLowerInvariant();
                            bool failingStepIsAzdDeploy =
                                System.Text.RegularExpressions.Regex.IsMatch(
                                    failingCmdLc, @"\bazd\s+(up|deploy)\b");

                            foreach (var ns in newSteps)
                            {
                                var c = (ns.Command ?? string.Empty);
                                var lc = c.ToLowerInvariant();
                                // 1) Replacing 'azd up'/'azd deploy' with
                                //    'azd provision' alone (no 'azd deploy'
                                //    follow-up) IS a degradation. When the
                                //    failing step isn't an azd-deploy step,
                                //    'azd provision' is a legitimate prereq
                                //    setup (see comment above) and we let
                                //    it through.
                                if (failingStepIsAzdDeploy
                                    && System.Text.RegularExpressions.Regex.IsMatch(
                                        lc, @"\bazd\s+provision\b")
                                    && !System.Text.RegularExpressions.Regex.IsMatch(
                                        lc, @"\bazd\s+deploy\b"))
                                {
                                    return true;
                                }
                                // 2) Setting *_RESOURCE_EXISTS=true to skip services —
                                //    rejected UNLESS accompanied by a real ACR build +
                                //    containerapp update in the same batch (legitimate
                                //    remote-build recovery, see above).
                                if (System.Text.RegularExpressions.Regex.IsMatch(
                                        lc, @"azure_service_[a-z0-9_]+_resource_exists\s+true")
                                    && !hasAcrBuildAndUpdate)
                                {
                                    return true;
                                }
                            }
                            return false;
                        }

                        if (fix.Kind == "replace_step" && DegradesDeploy(fix.NewSteps))
                        {
                            await Log(s, "warn",
                                "Rejected Doctor remediation: would degrade the deploy model " +
                                "(replacing 'azd up' with 'azd provision' alone, or setting " +
                                "AZURE_SERVICE_*_RESOURCE_EXISTS=true). Both leave Container " +
                                "Apps on the 'hello-world' placeholder. Retrying the original " +
                                "step so the Doctor has another chance to propose a real fix.",
                                step.Id);
                            // Skip the 'apply fix' branch entirely. The outer
                            // retry loop will re-invoke the Doctor with the
                            // same error next iteration.
                            fix = null;
                        }

                        // Near-duplicate detection: if the Doctor proposes a
                        // command materially identical to one it has already
                        // tried (same step target, tokens differ only in
                        // whitespace / quoting / short arg reshuffling) we
                        // reject it so the NEXT invocation sees it tagged
                        // as a "redundant attempt" and must pivot. Without
                        // this the Doctor can burn dozens of retries on
                        // micro-variations of the same wrong idea (we saw
                        // this on --mount=type=cache sed patterns: 20+
                        // slightly-different find/sed invocations that all
                        // produced the same broken Dockerfile).
                        if (fix is not null
                            && IsNearDuplicate(fix, previousAttempts, out var dupHint))
                        {
                            await Log(s, "warn",
                                $"Rejected Doctor remediation: near-duplicate of a prior " +
                                $"attempt ({dupHint}). The same strategy has already failed; " +
                                "the Doctor will be re-invoked with this attempt recorded in " +
                                "PREVIOUS ATTEMPTS so it pivots to a materially different fix.",
                                step.Id);
                            // Record the rejected attempt so the Doctor sees
                            // it on the next round and cannot propose it a
                            // third time without acknowledgement.
                            previousAttempts.Add(
                                $"step {step.Id} [{SummariseErrorSignature(stepTail)}] REJECTED_DUP: " +
                                Truncate(fix.NewSteps[0].Command, 100));
                            fix = null;
                        }

                        // Correctness guard: reject Doctor remediations
                        // that emit known-broken patterns (e.g.
                        // `az resource wait --created --name <hardcoded>`
                        // that will hang for 60 minutes, or `az acr wait`
                        // which is not a valid subcommand). Surface the
                        // violation Code in PREVIOUS_ATTEMPTS so the
                        // next Doctor pass sees WHY and pivots, instead
                        // of wasting a slot executing the broken step.
                        if (fix is not null)
                        {
                            AgentStationHub.Services.Security.CommandSafetyGuard.Violation? guardHit = null;
                            foreach (var ns in fix.NewSteps)
                            {
                                guardHit = AgentStationHub.Services.Security.CommandSafetyGuard.Validate(ns.Command);
                                if (guardHit is not null) break;
                            }
                            if (guardHit is not null)
                            {
                                await Log(s, "warn",
                                    $"Rejected Doctor remediation: [{guardHit.Code}] {guardHit.Reason} " +
                                    "Re-invoking Doctor with this attempt recorded so it pivots.",
                                    step.Id);
                                previousAttempts.Add(
                                    $"step {step.Id} [{SummariseErrorSignature(stepTail)}] " +
                                    $"REJECTED_GUARD[{guardHit.Code}]: " +
                                    Truncate(fix.NewSteps[0].Command, 100));
                                fix = null;
                            }
                        }
                    }

                    if (fix is not null && fix.Kind != "give_up" && fix.NewSteps.Count > 0)
                    {
                        // Include the error signature alongside the attempted
                        // remediation so the Doctor can detect "I've tried
                        // three different things against the same error, the
                        // strategy class is not working" and escalate to
                        // give_up instead of spinning.
                        var errSig = SummariseErrorSignature(stepTail);
                        previousAttempts.Add(
                            $"step {step.Id} [{errSig}]: {Truncate(step.Command, 60)} — " +
                            $"{fix.Kind} -> {Truncate(fix.NewSteps[0].Command, 80)}");

                        // Note: a signature-based circuit breaker used to
                        // live here ("same signature >= 3x -> hard abort").
                        // It was removed on purpose: for non-trivial failures
                        // (e.g. opaque azd preprovision hooks with empty
                        // stderr) the Doctor legitimately needs several
                        // speculative attempts to discover the actual cause.
                        // The budget + the Doctor's own 'give_up' remain the
                        // terminators. The signature is still tagged onto
                        // previousAttempts so the Doctor can see repetition
                        // and choose 'give_up' itself when warranted.

                        if (!string.IsNullOrWhiteSpace(fix.Reasoning))
                            await Log(s, "info", $"Doctor: {fix.Reasoning}", step.Id);

                        if (fix.Kind == "replace_step")
                        {
                            // Guard: the Doctor is supposed to target either
                            // the currently-failing step or a future one.
                            // Pointing at an already-executed step is
                            // meaningless (you cannot un-run history) and
                            // historically caused a catastrophic bug: the
                            // Doctor would say 'replace step 4' (an
                            // already-completed azd env set) and the
                            // orchestrator would replace step i (the
                            // failing azd up), destroying the actual
                            // deploy command and leaving only a corrective
                            // env var step.
                            //
                            // Policy:
                            //   • fix.StepId == step.Id  -> replace the failing step (original intent)
                            //   • fix.StepId points to a future step in 'steps' -> replace that one
                            //   • fix.StepId missing / points to a past step -> convert to insert_before
                            //     the failing step (safe corrective prepend)
                            var targetIndex = fix.StepId == step.Id
                                ? i
                                : steps.FindIndex(x => x.Id == fix.StepId);

                            if (targetIndex < 0 || targetIndex < i)
                            {
                                await Log(s, "warn",
                                    $"🩺 Doctor targeted step {fix.StepId} which is not replaceable " +
                                    $"(past or unknown). Converting to 'insert_before' step {step.Id} " +
                                    "to avoid dropping the failing step.",
                                    step.Id);
                                for (int k = 0; k < fix.NewSteps.Count; k++)
                                    steps.Insert(i + k, fix.NewSteps[k]);
                                await PublishUpdatedPlanAsync(s, plan, steps, ct);
                                i--;
                                continue;
                            }

                            await Log(s, "status",
                                $"🩺 Doctor applied 'replace_step': substituting step {steps[targetIndex].Id} " +
                                $"with `{Truncate(fix.NewSteps[0].Command, 80)}`. Re-running now.",
                                step.Id);
                            steps[targetIndex] = fix.NewSteps[0];
                            for (int k = 1; k < fix.NewSteps.Count; k++)
                                steps.Insert(targetIndex + k, fix.NewSteps[k]);
                            await PublishUpdatedPlanAsync(s, plan, steps, ct);
                            // 8.3: arm attribution. If the (now replaced)
                            // step exits 0 on the next iteration we will
                            // record (errSig -> command) as a proven fix.
                            pendingDoctorAttribution = (errSig, fix.NewSteps[0].Command ?? string.Empty);
                            // Re-execute starting from the replaced step,
                            // not from i, when the Doctor retargeted a
                            // future step — we want to run that one now.
                            i = Math.Min(i, targetIndex) - 1;
                            continue;
                        }
                        if (fix.Kind == "insert_before")
                        {
                            var preview = string.Join(
                                ", ",
                                fix.NewSteps.Take(2).Select(x => Truncate(x.Command, 40)));
                            await Log(s, "status",
                                $"🩺 Doctor applied 'insert_before': adding {fix.NewSteps.Count} " +
                                $"prep step(s) ({preview}) ahead of step {step.Id}. " +
                                "Running prep steps now, then retrying the failed step.",
                                step.Id);
                            for (int k = 0; k < fix.NewSteps.Count; k++)
                                steps.Insert(i + k, fix.NewSteps[k]);
                            await PublishUpdatedPlanAsync(s, plan, steps, ct);
                            // 8.3: arm attribution against the first prep
                            // step's command. If it exits 0 we record it.
                            pendingDoctorAttribution = (errSig, fix.NewSteps[0].Command ?? string.Empty);
                            i--; // step at position i is now the first prep step
                            continue;
                        }
                        // Unknown kind → treat as give_up.
                    }
                    else if (fix is { Kind: "give_up" })
                    {
                        // The Doctor walked away. Two flavours:
                        //   (a) regular give_up — we failed and we don't
                        //       know how to recover; surfaced as 'err'
                        //       and the session ends Failed.
                        //   (b) [Escalate] verdict — the repo source is
                        //       the bug; the pipeline did its job and the
                        //       next move is on the user. Surfaced as
                        //       'info' (NOT 'err') and the session ends
                        //       BlockedNeedsHumanOrSourceFix so the UI
                        //       renders an info alert instead of the red
                        //       'Deployment error' box.
                        var isEscalate = !string.IsNullOrWhiteSpace(fix.Reasoning)
                            && fix.Reasoning!.TrimStart().StartsWith("[Escalate]",
                                StringComparison.OrdinalIgnoreCase);

                        // ----------------------------------------------------------
                        // Auto-patch override: when the Doctor escalates because the
                        // repo's Bicep/parameters/azure.yaml references a deprecated
                        // OpenAI model name, the fix is mechanical (sed + retry) and
                        // we should NOT bounce the user out to "open a PR on the
                        // upstream sample". Synthesise an insert_before remediation
                        // here, apply it inline, and continue the loop. The Doctor's
                        // job is to produce a remediation; when its hosted backend
                        // refuses, the orchestrator fills the gap deterministically.
                        // ----------------------------------------------------------
                        var autoPatch = TryAutoPatchEscalation(
                            fix.Reasoning ?? string.Empty, stepTail, step.Id, step.Command ?? string.Empty);

                        // ----------------------------------------------------------
                        // Long-tail fallback: if the deterministic auto-patch table
                        // didn't match, ask the EscalationResolverAgent (Meta-Doctor)
                        // for an LLM-synthesised fix BEFORE we surface either the
                        // BlockedNeedsHumanOrSourceFix verdict OR a regular
                        // 'Deployment error'. The Resolver sees the failing
                        // command, log tail, Doctor's reasoning, and the list of
                        // previous attempts; it can return a `replace_step` or
                        // `insert_before` Remediation, or give_up (in which case
                        // we proceed to the original failure path). This makes
                        // every Doctor give_up — escalate or otherwise — a chance
                        // for the orchestrator to self-heal: e.g. when Doctor
                        // reasons "switch to remote build approach" but emits
                        // newSteps=[], the Resolver turns that hint into an
                        // executable bash step.
                        // ----------------------------------------------------------
                        if (autoPatch is null)
                        {
                            try
                            {
                                // Prefer the Foundry-hosted EscalationResolver
                                // when registered (feature flag
                                // Foundry:UseFoundryEscalationResolver=true).
                                // Falls back to the in-process agent otherwise.
                                var hosted = _sp.GetService<AgentStationHub.Services.Tools.FoundryEscalationResolverClient>();
                                if (hosted is not null)
                                {
                                    await Log(s, "info",
                                        (isEscalate
                                            ? "Doctor escalated"
                                            : "Doctor gave up")
                                        + " and no deterministic auto-patch matched — "
                                        + "consulting Foundry-hosted EscalationResolver agent…",
                                        step.Id);
                                    autoPatch = await hosted.ResolveAsync(
                                        failingStep: step,
                                        failingCommand: step.Command ?? string.Empty,
                                        stepTail: stepTail,
                                        doctorReasoning: fix.Reasoning ?? string.Empty,
                                        previousAttempts: previousAttempts.ToList(),
                                        ct: ct,
                                        azureRegion: s.AzureLocation);
                                }
                                else
                                {
                                    var resolver = _sp.GetService<AgentStationHub.Services.Agents.EscalationResolverAgent>();
                                    if (resolver is not null)
                                    {
                                        await Log(s, "info",
                                            (isEscalate
                                                ? "Doctor escalated"
                                                : "Doctor gave up")
                                            + " and no deterministic auto-patch matched — "
                                            + "consulting in-process EscalationResolver agent…",
                                            step.Id);
                                        autoPatch = await resolver.ResolveAsync(
                                            failingStep: step,
                                            failingCommand: step.Command ?? string.Empty,
                                            stepTail: stepTail,
                                            doctorReasoning: fix.Reasoning ?? string.Empty,
                                            previousAttempts: previousAttempts.ToList(),
                                            ct: ct,
                                            azureRegion: s.AzureLocation);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex,
                                    "EscalationResolver invocation failed; falling back to escalate path.");
                            }
                        }
                        if (autoPatch is not null && autoPatch.NewSteps is { Count: > 0 })
                        {
                            var applyKind = string.Equals(
                                autoPatch.Kind, "replace_step", StringComparison.OrdinalIgnoreCase)
                                ? "replace_step" : "insert_before";

                            await Log(s, "status",
                                $"🩺 " + (isEscalate ? "Doctor escalated" : "Doctor gave up")
                                + ", but the orchestrator recognised the "
                                + $"failure signature as auto-patchable. Applying synthesised "
                                + $"{applyKind}: {autoPatch.NewSteps[0].Description}",
                                step.Id);
                            previousAttempts.Add(
                                $"step {step.Id} [{SummariseErrorSignature(stepTail)}] " +
                                $"AUTO_PATCH/{applyKind}: {Truncate(autoPatch.NewSteps[0].Command, 100)}");

                            if (applyKind == "replace_step")
                            {
                                // Replace the failing step with the first new step,
                                // then insert any remaining new steps right after it.
                                steps[i] = autoPatch.NewSteps[0];
                                for (int k = 1; k < autoPatch.NewSteps.Count; k++)
                                    steps.Insert(i + k, autoPatch.NewSteps[k]);
                            }
                            else
                            {
                                // insert_before: keep the original failing step in
                                // place and insert the new steps before it.
                                for (int k = 0; k < autoPatch.NewSteps.Count; k++)
                                    steps.Insert(i + k, autoPatch.NewSteps[k]);
                            }
                            await PublishUpdatedPlanAsync(s, plan, steps, ct);
                            // 8.3: arm attribution for the auto-patch / resolver fix.
                            pendingDoctorAttribution = (
                                SummariseErrorSignature(stepTail),
                                autoPatch.NewSteps[0].Command ?? string.Empty);
                            i--; // re-execute starting at the (now patched) index
                            continue;
                        }

                        await Log(s, isEscalate ? "info" : "err",
                            $"Doctor gave up: {fix.Reasoning ?? "(no reason provided)"}",
                            step.Id);

                        // Persist the give-up rationale so the next deploy
                        // of the same repo sees it under prior insights
                        // and can short-circuit equivalent dead ends.
                        if (!string.IsNullOrWhiteSpace(fix.Reasoning))
                        {
                            _memory.UpsertInsight(s.RepoUrl, "doctor.lastGiveUp",
                                fix.Reasoning, confidence: 0.7);
                        }

                        // 8.8: per-signature failure store. Used at the
                        // START of the next Doctor invocation for the same
                        // repo+errSig (in this or a future session) to
                        // prefill previousAttempts so the Doctor pivots
                        // instead of re-trying known dead-ends.
                        var giveUpSig = SummariseErrorSignature(stepTail);
                        if (!string.IsNullOrEmpty(giveUpSig))
                        {
                            var attemptsBlob = string.Join(
                                "\n",
                                previousAttempts.TakeLast(8)
                                                .Select(a => Truncate(a, 180)));
                            if (!string.IsNullOrWhiteSpace(attemptsBlob))
                            {
                                try
                                {
                                    _memory.UpsertInsight(
                                        s.RepoUrl,
                                        $"doctor.giveup.{giveUpSig}",
                                        attemptsBlob,
                                        confidence: 0.7);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogDebug(ex,
                                        "Failed to persist doctor.giveup insight (non-fatal).");
                                }
                            }
                        }

                        // [Escalate] verdict — the Doctor has determined that
                        // the failure is in the REPO SOURCE itself (missing
                        // Dockerfile, broken Bicep, corrupt lockfile) and
                        // continuing to retry inside the sandbox cannot
                        // succeed. Surface this to the UI as a distinct
                        // BlockedNeedsHumanOrSourceFix signal so the user
                        // can either (a) apply a fix on the source repo
                        // and retry, or (b) skip the offending service.
                        if (isEscalate)
                        {
                            await Log(s, "info",
                                "Doctor emitted an [Escalate] verdict: the failure is in the " +
                                "repository source and cannot be patched from inside the sandbox. " +
                                "Apply the proposed fix on the repo (PR / commit) and retry the " +
                                "deploy.",
                                step.Id);
                            s.ErrorMessage =
                                $"BlockedNeedsHumanOrSourceFix: step #{step.Id} " +
                                $"'{step.Description}' — {fix.Reasoning}";
                            await SetStatus(s, DeploymentStatus.BlockedNeedsHumanOrSourceFix);
                            return;
                        }
                    }
                }

                s.ErrorMessage =
                    $"Step {step.Id} '{step.Description}' failed (exit code {result.ExitCode}).\n" +
                    $"Command: {step.Command}\n" +
                    $"--- last output ---\n{stepTail}";
                await Log(s, "err", s.ErrorMessage, step.Id);
                await SetStatus(s, DeploymentStatus.Failed);
                return;
            }

            await SetStatus(s, DeploymentStatus.Verifying);
            var tail = string.Join('\n', s.Logs.TakeLast(40).Select(l => l.Message));

            // Real-world post-deploy probe: even when every step exited 0,
            // a deploy can be "hollow" — the template provisioned Container
            // Apps but they're all still pointing at Microsoft's default
            // 'containerapps-helloworld' placeholder (ACR empty, no real
            // image build+push happened). We've hit this when the Doctor
            // navigated around a missing docker runtime by skipping the
            // package+deploy phases. Surface that fact to the Verifier as
            // a hint so it doesn't blindly mark the deploy Succeeded.
            var probeHints = await ProbeDeployedStateAsync(s, ct);
            var allHints = plan.VerifyHints.Concat(probeHints).ToList();

            var v = await verifier.VerifyAsync(tail, allHints, ct);
            s.FinalEndpoint = v.Endpoint;
            if (!string.IsNullOrEmpty(v.Notes)) await Log(s, "info", $"Verifier: {v.Notes}");

            // If our probe spotted placeholder images OR zero Container
            // Apps at all, take recovery into our own hands regardless
            // of what the Verifier concluded. This is a deterministic
            // signal that cannot be argued away by interpretation of
            // the log tail. We previously gated on `v.Success` only,
            // but that left the "0 CAs" case stuck on a Failed verdict
            // even though the recovery (re-run azd up) can usually
            // unstick it.
            var hasHollowSignal = probeHints.Any(h =>
                h.StartsWith("hollow_deploy:", StringComparison.Ordinal));
            if (hasHollowSignal)
            {
                var hollowDetail = string.Join("; ",
                    probeHints.Where(h => h.StartsWith("hollow_deploy:")));
                var noCasCase = probeHints.Any(h =>
                    h.StartsWith("hollow_deploy:no_cas",
                                 StringComparison.Ordinal));

                // Autonomous recovery: instead of immediately failing the
                // whole deploy, try an 'azd deploy --no-prompt' pass. The
                // infrastructure is already intact (the post-deploy probe
                // proved it), so all we need is to drive azd through the
                // package + push + update-container-app phases again. This
                // typically succeeds when the earlier 'azd up' exited
                // non-zero because of a parallel-provision race (some
                // Container Apps were still in 'Validation timed out' at
                // the moment azd gave up) but the follow-up provision
                // (triggered by our ContainerAppValidationTimeout auto-
                // retry) stabilised them — just that the FINAL deploy
                // phase never ran.
                //
                // We only attempt this ONCE per session: if it doesn't
                // fix the hollow state, we mark Failed so the user knows.
                // This is a terminal recovery pass, not another retry
                // loop — the loop responsibility lives in step-level
                // auto-retry + Doctor remediation ABOVE, not here.
                await Log(s, "warn",
                    (v.Success
                        ? "Verifier said Succeeded but the real Azure state disagrees"
                        : "Verifier reported failure")
                    + $": {hollowDetail}. " +
                    "Triggering an autonomous "
                    + (noCasCase ? "'azd up'" : "'azd deploy'")
                    + " recovery pass before failing the deploy.");

                var recovered = await TryAutoRecoverHollowDeployAsync(
                    s, docker, plan.Environment, ct, runFullAzdUp: noCasCase);

                if (recovered)
                {
                    // Re-probe: did the recovery actually move the needle?
                    var reprobeHints = await ProbeDeployedStateAsync(s, ct);
                    var stillHollow = reprobeHints.Any(h =>
                        h.StartsWith("hollow_deploy:", StringComparison.Ordinal));
                    if (!stillHollow)
                    {
                        await Log(s, "info",
                            "Post-recovery probe: all Container Apps now serving real images. " +
                            "Deploy marked Succeeded.");
                        await SetStatus(s, DeploymentStatus.Succeeded);
                        _memory.UpsertInsight(s.RepoUrl, "lastSuccess.azureLocation",
                            s.AzureLocation, confidence: 1.0);
                        _memory.UpsertInsight(s.RepoUrl, "lastSuccess.at",
                            DateTimeOffset.UtcNow.ToString("O"), confidence: 1.0);
                        if (!string.IsNullOrEmpty(v.Endpoint))
                            _memory.UpsertInsight(s.RepoUrl, "lastSuccess.endpoint",
                                v.Endpoint, confidence: 1.0);
                        return;
                    }
                    // Still hollow after recovery — fall through to Failed.
                    hollowDetail = string.Join("; ",
                        reprobeHints.Where(h => h.StartsWith("hollow_deploy:")));
                }

                s.ErrorMessage =
                    "Deploy completed infrastructure provisioning but the application images " +
                    "were never built or pushed after the autonomous recovery pass. " +
                    $"Detail: {hollowDetail}. Inspect the Live log for 'azd deploy' errors " +
                    "from the recovery pass; typical causes are an inaccessible ACR, a " +
                    "Dockerfile build error, or missing buildx.";
                await SetStatus(s, DeploymentStatus.Failed);
                return;
            }

            await SetStatus(s, v.Success ? DeploymentStatus.Succeeded : DeploymentStatus.Failed);

            // Distil outcome into durable insights the next deploy of the
            // same (or similar) repo can benefit from.
            if (v.Success)
            {
                _memory.UpsertInsight(s.RepoUrl, "lastSuccess.azureLocation",
                    s.AzureLocation, confidence: 1.0);
                _memory.UpsertInsight(s.RepoUrl, "lastSuccess.at",
                    DateTimeOffset.UtcNow.ToString("O"), confidence: 1.0);
                if (!string.IsNullOrEmpty(v.Endpoint))
                    _memory.UpsertInsight(s.RepoUrl, "lastSuccess.endpoint",
                        v.Endpoint, confidence: 1.0);
            }
            else if (!string.IsNullOrEmpty(v.Notes))
            {
                _memory.UpsertInsight(s.RepoUrl, "lastFailure.verifier",
                    v.Notes, confidence: 0.6);
            }
        }
        catch (OperationCanceledException)
        {
            // The only sources of OperationCanceledException here are:
            //   1. user click on the Cancel button (s.Cts.Cancel())
            //   2. a per-step timeout firing inside DockerShellTool
            // In both cases the running docker container is already being
            // torn down by CliWrap, so we just record a concise reason.
            if (s.Cts.IsCancellationRequested)
            {
                s.ErrorMessage = "Cancelled by user.";
            }
            else
            {
                // Concise timeout message. The earlier verbose "what to do"
                // block was duplicated between the live log panel and the
                // red error card in the UI, producing a wall of text the
                // user had to scroll past every time a step hit its budget.
                // We keep the essential facts (which step, how long, what
                // budget) on a single line. Context-specific advice
                // ('check Azure portal', 'az group delete is long-running')
                // is already in the README + the step-timeout warning
                // already logged minutes earlier by the heartbeat task, so
                // repeating it here adds noise without information.
                var elapsed = currentStepStartedAt.HasValue
                    ? DateTime.UtcNow - currentStepStartedAt.Value
                    : TimeSpan.Zero;
                var stepSummary = currentStep is not null
                    ? $"step {currentStep.Id} ('{Truncate(currentStep.Description, 80)}')"
                    : "a deployment step";

                // With timeouts set to Infinite by default, hitting this
                // branch means the user clicked Cancel or an upstream
                // cancellation fired (docker daemon gone, session GC'd).
                // Phrase the message accordingly; only mention a numeric
                // budget when one was actually configured.
                var hasFiniteBudget = currentStepBudget > TimeSpan.Zero
                    && currentStepBudget != System.Threading.Timeout.InfiniteTimeSpan;
                if (hasFiniteBudget)
                {
                    s.ErrorMessage =
                        $"Timeout: {stepSummary} did not finish within " +
                        $"{currentStepBudget.TotalMinutes:0} min (elapsed " +
                        $"{elapsed.TotalMinutes:0} min). The work may still be running on Azure; " +
                        "check the portal before retrying.";
                }
                else
                {
                    s.ErrorMessage =
                        $"Cancelled: {stepSummary} was stopped after " +
                        $"{elapsed.TotalMinutes:0} min. The work may still be running on Azure; " +
                        "check the portal before retrying.";
                }
            }
            await Log(s, "err", s.ErrorMessage);
            await SetStatus(s, DeploymentStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Deployment {Id} failed", s.Id);
            s.ErrorMessage = ex.Message;
            await SetStatus(s, DeploymentStatus.Failed);
        }
        finally
        {
            // Tear down the long-lived sandbox container FIRST, before the
            // workspace volume: docker refuses to delete a volume that is
            // still referenced by an existing container, even a stopped
            // one. Both teardowns run with CancellationToken.None so a
            // user-cancel doesn't skip cleanup of resources we created.
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Failed to dispose sandbox session container for {Id} (non-fatal).", s.Id);
                }
            }

            try
            {
                await SandboxWorkspaceVolume.RemoveAsync(s.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to remove workspace volume for session {Id} (non-fatal).", s.Id);
            }
        }
    }

    private async Task SetStatus(DeploymentSession s, DeploymentStatus st)
    {
        s.Status = st;
        _store.SaveLater(s);
        await _hub.Clients.Group(s.Id).SendAsync("StatusChanged", st.ToString(), s.ErrorMessage, s.FinalEndpoint);
    }

    private async Task Log(DeploymentSession s, string level, string message, int? stepId = null)
    {
        var e = new LogEntry(DateTime.UtcNow, level, message, stepId);
        s.Logs.Add(e);
        _store.SaveLater(s);
        await _hub.Clients.Group(s.Id).SendAsync("LogAppended", e);

        // Record agent traces in the memory store so the per-session
        // history is reconstructable from disk after the fact (useful for
        // debugging a failed deploy the user already closed the tab on).
        // We only persist lines coming from the sandbox runner — the
        // orchestrator's own status messages are already summarised in
        // s.Logs and would just duplicate noise.
        if (!string.IsNullOrEmpty(message) && message.StartsWith("[agent-runner]", StringComparison.Ordinal))
        {
            var trimmed = message.Substring("[agent-runner]".Length).Trim();
            // Parse '[agent] <Name>/<role>: <content>' format emitted by PlanningTeam.
            var m = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^\[agent\]\s+(?<name>[A-Za-z0-9_\-]+)/(?<role>[a-z]+):\s*(?<body>.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success)
                _memory.RecordTurn(s.Id, m.Groups["name"].Value, m.Groups["role"].Value, m.Groups["body"].Value);
            else
                _memory.RecordTurn(s.Id, "runner", level, trimmed);
        }
    }

    private static string? ReadHostTenantId() => ReadHostDefaultSubscription().TenantId;

    /// <summary>
    /// Light validation for user-supplied tenant / subscription ids. We
    /// accept a GUID (with or without surrounding whitespace / braces)
    /// and reject anything else by returning null, so a stray paste of
    /// a domain name or display name doesn't get propagated to
    /// 'az login --tenant ...' where it would cause a confusing
    /// "tenant 'foo' not found" loop.
    /// </summary>
    private static string? NormalizeGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().Trim('{', '}');
        return Guid.TryParse(t, out var g) ? g.ToString() : null;
    }

    /// <summary>
    /// Runs 'docker version --format "{{.Server.Version}}"' to verify the
    /// Docker daemon is reachable BEFORE any other pipeline phase starts.
    /// Returns null when Docker is healthy, or a short error message when
    /// the client cannot talk to the daemon (Docker Desktop not started,
    /// npipe/unix socket missing, bad DOCKER_HOST env var, etc.).
    ///
    /// Short timeout (5s): 'docker version' is local-only, if it has not
    /// answered in 5 seconds the daemon is effectively down.
    /// </summary>
    private static async Task<string?> PreflightDockerAsync(CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format \"{{.Server.Version}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return "failed to start 'docker' CLI (is it on PATH?)";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return "'docker version' timed out after 5s (daemon unresponsive).";
            }

            if (proc.ExitCode != 0)
            {
                var stderr = (await proc.StandardError.ReadToEndAsync(ct)).Trim();
                // Keep only the first non-empty line: 'docker version'
                // emits a long 'Client: ...' block before the failure
                // message, and the user only needs the actionable tail.
                var firstLine = stderr.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => !string.IsNullOrEmpty(l)
                                      && !l.StartsWith("Client", StringComparison.OrdinalIgnoreCase))
                    ?? stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => !string.IsNullOrEmpty(l))
                    ?? "";
                return string.IsNullOrEmpty(firstLine)
                    ? $"'docker version' exited with code {proc.ExitCode}."
                    : firstLine;
            }
            return null; // Healthy.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "'docker' executable not found on PATH.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Classifies a step's tail output and returns a short, user-facing
    /// message when the failure is environmental (Docker daemon down, az
    /// CLI unusable, sandbox image missing, DNS broken, …) — i.e. not
    /// something the in-sandbox Doctor can possibly fix. Returns null
    /// when the failure looks like a genuine deployment issue the Doctor
    /// should analyse. Keeping this list conservative: misclassifying a
    /// Doctor-fixable issue as "environmental" would bypass a valid
    /// remediation.
    /// </summary>
    private static string? DetectEnvironmentalFailure(string stepTail)
    {
        if (string.IsNullOrWhiteSpace(stepTail)) return null;
        var t = stepTail.ToLowerInvariant();

        // Docker daemon unreachable: by far the most common "cannot fix
        // from inside a container" failure. The client tries npipe on
        // Windows, /var/run/docker.sock on Linux, and any failure maps
        // to one of these strings.
        if (t.Contains("cannot connect to the docker daemon")
         || t.Contains("failed to connect to the docker api")
         || t.Contains("the system cannot find the file specified"
                        /* npipe missing */) && t.Contains("docker"))
        {
            return "the Docker daemon is not reachable (is Docker Desktop running?).";
        }

        // Docker image for the sandbox was never pulled / built.
        if (t.Contains("unable to find image") && t.Contains("locally"))
            return "the required sandbox image is missing from the Docker host.";

        // Azure RBAC: the signed-in identity lacks permissions to
        // create role assignments / validate the deployment. Bicep
        // templates that include `Microsoft.Authorization/roleAssignments`
        // resources require the caller to have Owner / RBAC Admin /
        // User Access Administrator on the target scope. The Doctor
        // cannot fix this � it requires a human with elevated rights
        // on the subscription / resource group to grant the role.
        if (t.Contains("authorizationfailed")
            || (t.Contains("does not have authorization to perform action")
                && t.Contains("microsoft."))
            || t.Contains("do not have sufficient permissions for this deployment")
            || (t.Contains("microsoft.authorization/roleassignments/write")
                && t.Contains("does not have permission")))
        {
            return "the signed-in Azure identity lacks the RBAC role required by this " +
                   "deployment (typically 'Owner', 'User Access Administrator', or 'Role " +
                   "Based Access Control Administrator' on the target subscription / " +
                   "resource group, because the template creates role assignments). " +
                   "Ask a subscription Owner to assign one of those roles to your " +
                   "account, then retry the deploy. Cached credentials are unaffected; " +
                   "no re-login required after the role is granted.";
        }

        // Azure subscription disabled / past due / over quota at the
        // subscription level � also un-fixable from inside the sandbox.
        if (t.Contains("subscriptionnotregistered")
            || t.Contains("subscription is disabled")
            || t.Contains("subscriptionnotfound"))
        {
            return "the target Azure subscription is disabled, not registered for the " +
                   "required resource provider, or not found. Re-check the subscription " +
                   "id in the Hub UI and confirm with your Azure admin that the sub is " +
                   "active and registered for the providers used by this template.";
        }

        return null;
    }

    /// <summary>
    /// Detects whether a command would invoke PowerShell. The sandbox
    /// image carries only bash, so any 'pwsh' / 'powershell' invocation
    /// exits 127. Matches both the bare binary name and the common
    /// 'pwsh -c' / '-Command' forms while avoiding false positives on
    /// substrings like 'pwshell' appearing inside longer words.
    /// </summary>
    private static bool ContainsPwsh(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            cmd,
            @"(^|[\s""'|&;])(pwsh|powershell)(\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// True iff <paramref name="cmd"/> contains a raw 'azd up' invocation
    /// (with or without flags) that is NOT already wrapped by the baked
    /// 'agentic-azd-up' helper. Conservative: requires 'azd' followed by
    /// 'up' as separate whitespace-delimited tokens.
    /// </summary>
    private static bool ContainsRawAzdUp(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        // Already using the baked helper — leave alone.
        if (cmd.Contains("agentic-azd-up", StringComparison.OrdinalIgnoreCase))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            cmd,
            @"(^|[\s""'|&;])azd\s+up(\s|$|"")",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Replace the leftmost 'azd up [args...]' span in <paramref name="cmd"/>
    /// with 'agentic-azd-up'. The baked helper takes no args and reads the
    /// same azd env, so any trailing flags (e.g. --no-prompt, -e ENV) are
    /// dropped — they're already the helper's defaults. Wrapping shells
    /// ('bash -lc "..."', 'sh -c "..."') and surrounding env assignments
    /// are preserved verbatim.
    /// </summary>
    private static string RewriteAzdUpToBakedHelper(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return cmd;
        // Match 'azd up' + optional flag tail up to the first unescaped
        // closing quote / shell separator. Conservative: stop at " ' &
        // ; | or end-of-string. Captures and discards the tail.
        var rx = new System.Text.RegularExpressions.Regex(
            @"\bazd\s+up(\s+[^""'&;|]*)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return rx.Replace(cmd, "agentic-azd-up", 1);
    }

    /// <summary>
    /// True if the command actually invokes 'azd' (not just mentions it).
    /// </summary>
    private static bool IsAzdInvocation(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            cmd, @"(^|[\s""'|&;])azd(\s|$|"")",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// True if the command is an `azd auth login` invocation. We detect
    /// these specifically to replace them with a cheap `az account show`
    /// because the sandbox image is configured for az-cli delegated
    /// auth, where `azd auth login` is hard-disabled.
    /// </summary>
    private static bool IsAzdAuthLogin(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            cmd, @"(^|[\s""'|&;])azd\s+auth\s+login\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    private static bool ContainsAzureEnvNameExport(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        return cmd.Contains("AZURE_ENV_NAME=", StringComparison.Ordinal);
    }

    /// <summary>
    /// Inject `export AZURE_ENV_NAME=&lt;name&gt;;` so every azd subcommand
    /// inside the bash one-liner sees the pre-resolved env name and never
    /// falls back to the interactive "Enter a unique environment name"
    /// prompt loop. Handles `bash -lc "..."` wrappers and bare commands.
    /// </summary>
    private static string PrefixAzureEnvNameExport(string cmd, string azdEnvName)
    {
        var exportClause = $"export AZURE_ENV_NAME={azdEnvName}; ";
        var trimmed = (cmd ?? "").Trim();
        var bashLc = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"^bash\s+-l?c\s+""(.+)""\s*$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (bashLc.Success)
        {
            var inner = bashLc.Groups[1].Value.Replace("\\\"", "\"");
            inner = exportClause + inner;
            return $"bash -lc \"{inner.Replace("\"", "\\\"")}\"";
        }
        return $"bash -lc \"{exportClause}{(cmd ?? "").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// Pre-execution hardening for `azd init -t <template>` steps. The
    /// command has TWO interactive prompts that wedge in headless
    /// pipelines: (a) "Continue initializing an app in '/workspace'?
    /// (y/N)" when the working dir already contains the cloned repo
    /// (always, in our flow), and (b) "Enter a unique environment
    /// name". On some `yes`-piped pipelines we observed the second
    /// prompt reading EMPTY strings in a tight loop � only the silence
    /// cap rescued the session. The deterministic fix is to inject
    /// `-e &lt;envname&gt;` (using the env name the orchestrator already
    /// resolved from the plan) and `--no-prompt`, then prefix with
    /// `yes |` for older azd builds whose --no-prompt still asks for
    /// confirmation. Idempotent: returns false (no rewrite needed)
    /// when all three are already present.
    /// </summary>
    private static bool HardenAzdInit(string? cmd, string? azdEnvName, out string hardened)
    {
        hardened = cmd ?? "";
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                cmd, @"\bazd\s+init\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;

        // Strip an existing 'bash -lc "..."' wrapper if present so we don't
        // end up with nested quotes when we re-wrap at the end.
        var inner = cmd.Trim();
        var bashLc = System.Text.RegularExpressions.Regex.Match(
            inner,
            @"^bash\s+-l?c\s+""(.+)""\s*$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var hadBashWrap = bashLc.Success;
        if (hadBashWrap)
            inner = bashLc.Groups[1].Value.Replace("\\\"", "\"");

        var changed = false;

        // Inject -e <envname> if missing.
        var hasEnvFlag = System.Text.RegularExpressions.Regex.IsMatch(
            inner, @"\b(-e|--environment)\s+\S+");
        if (!hasEnvFlag && !string.IsNullOrWhiteSpace(azdEnvName))
        {
            inner = System.Text.RegularExpressions.Regex.Replace(
                inner,
                @"\bazd\s+init\b",
                $"azd init -e {azdEnvName}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            changed = true;
        }

        // IMPORTANT: do NOT inject --no-prompt on `azd init -t`. When the
        // working dir is non-empty (always, in our flow: we clone the
        // user repo first, then `azd init -t <template>` scaffolds the
        // sample template ON TOP), --no-prompt makes azd AUTO-DECLINE
        // the "Continue? (y/N)" prompt with "confirmation declined; app
        // was not initialized" and exit 1. We need azd to ASK the
        // prompt so that our `yes |` pipe can answer 'y'. Strip any
        // pre-existing --no-prompt flag the planner may have added.
        if (System.Text.RegularExpressions.Regex.IsMatch(inner, @"\s--no-prompt\b"))
        {
            inner = System.Text.RegularExpressions.Regex.Replace(
                inner, @"\s+--no-prompt\b", "");
            changed = true;
        }

        // Prefix with `yes |` if missing.
        var trimmed = inner.TrimStart();
        if (!trimmed.StartsWith("yes ", StringComparison.Ordinal)
            && !trimmed.StartsWith("yes\t", StringComparison.Ordinal)
            && !inner.Contains("yes |", StringComparison.Ordinal))
        {
            inner = "yes | " + inner;
            changed = true;
        }

        if (!changed) return false;

        hardened = hadBashWrap
            ? $"bash -lc \"{inner.Replace("\"", "\\\"")}\""
            : (cmd.StartsWith("bash ", StringComparison.Ordinal) ? inner : $"bash -lc \"{inner.Replace("\"", "\\\"")}\"");
        return true;
    }

    /// <summary>
    /// Best-effort rewrite of a pwsh-based command to its bash equivalent.
    /// Handles the patterns the Strategist actually emits:
    ///   pwsh -c "..."             -> bash -lc "..."
    ///   powershell -Command "..." -> bash -lc "..."
    /// </summary>
    private static string RewritePwshToBash(string cmd)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"\b(pwsh|powershell)\s+(-c|-Command)\s+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rx.IsMatch(cmd)) return rx.Replace(cmd, "bash -lc ");

        // Bare 'pwsh -something' / 'pwsh script.ps1': wrap in bash -lc so
        // the outer shell at least exists, producing a clearer error for
        // the Doctor if the body is actually PowerShell-specific.
        return $"bash -lc \"{cmd.Replace("\"", "\\\"")}\"";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    /// <summary>
    /// Heuristic: is the Doctor's proposed remediation a near-duplicate of
    /// something already on the previousAttempts list? Trivial exact-match
    /// is not enough (LLMs routinely shuffle whitespace, swap single
    /// quotes for double, reorder short flags) so we compare a NORMALISED
    /// token signature instead.
    ///
    /// Returns true + a short user-facing reason when a match is found.
    /// The orchestrator uses this to reject the remediation and force the
    /// Doctor to pivot on the next iteration.
    /// </summary>
    private static bool IsNearDuplicate(
        AgentStationHub.Models.Remediation fix,
        IReadOnlyList<string> previousAttempts,
        out string reason)
    {
        reason = "";
        if (fix.NewSteps.Count == 0) return false;

        static string Normalise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Collapse whitespace + strip the three flavours of quote
            // + lowercase + drop the common 'bash -lc' / 'sh -c' wrapper
            // so the comparison focuses on the actual intent.
            var core = System.Text.RegularExpressions.Regex.Replace(
                s.Trim(), @"^(bash|sh)\s+-l?c\s+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            core = core.Replace("\"", "").Replace("'", "").Replace("`", "");
            core = System.Text.RegularExpressions.Regex.Replace(core, @"\s+", " ");
            return core.ToLowerInvariant();
        }

        // Extract the proposed commands (all steps in this remediation)
        // as a single joined signature.
        var proposed = string.Join(" && ",
            fix.NewSteps.Select(ns => Normalise(ns.Command ?? "")));
        if (proposed.Length < 8) return false;

        // Tokenise so we can compute a Jaccard similarity — robust against
        // small arg reorderings that plain string equality misses.
        static HashSet<string> Tokens(string s) =>
            new HashSet<string>(s.Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var proposedTokens = Tokens(proposed);
        if (proposedTokens.Count < 3) return false;

        foreach (var prior in previousAttempts)
        {
            // previousAttempts lines look like:
            //   "step 10 [ErrSig]: <original cmd> — replace_step -> <new cmd>"
            // We're interested in the PART AFTER the last '->' (the fix
            // the Doctor previously proposed) since a duplicate of the
            // original failing command isn't meaningful — it's the FIX
            // that's repeating.
            var arrow = prior.LastIndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var priorCmd = Normalise(prior[(arrow + 2)..]);
            if (priorCmd.Length < 8) continue;

            // Exact normalised equality — the most common LLM re-proposal.
            if (priorCmd.Equals(proposed, StringComparison.Ordinal))
            {
                reason = "exact command match (ignoring whitespace/quotes)";
                return true;
            }

            // Jaccard similarity >= 0.85 flags "same intent, minor reshuffle"
            // without false-positiving on commands that happen to share a
            // 'find /workspace' or 'sed -i' prefix.
            var priorTokens = Tokens(priorCmd);
            if (priorTokens.Count < 3) continue;
            var inter = proposedTokens.Intersect(priorTokens).Count();
            var union = proposedTokens.Union(priorTokens).Count();
            if (union == 0) continue;
            var similarity = (double)inter / union;
            if (similarity >= 0.85)
            {
                reason = $"{similarity:P0} token overlap with a prior attempt";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Post-deploy reality check. Queries the Azure Resource Group the
    /// deploy landed in and flags the session when Container Apps are
    /// still pointing at the 'containerapps-helloworld' placeholder image
    /// — which is what happens when the 'azd up' flow skips the
    /// package+deploy phases (typically because the Doctor navigated
    /// around a missing docker runtime by setting *_RESOURCE_EXISTS=true
    /// or demoting to 'azd provision' alone).
    ///
    /// Returns 0..N string hints to append to the Verifier's input. When
    /// a hollow deploy is detected we emit a "hollow_deploy:..." hint
    /// that the orchestrator turns into a hard Failed status — even if
    /// the Verifier LLM would otherwise interpret the clean exit codes
    /// as success.
    ///
    /// The probe is best-effort: if we can't extract a RG name from the
    /// logs, or 'az' isn't reachable, we return an empty hint list and
    /// let the Verifier decide on its own. We never throw.
    /// </summary>
    private async Task<bool> TryAutoRecoverHollowDeployAsync(
        DeploymentSession s,
        Tools.DockerShellTool docker,
        IReadOnlyDictionary<string, string> planEnv,
        CancellationToken ct,
        bool runFullAzdUp = false)
    {
        try
        {
            await Log(s, "status",
                "Autonomous recovery: running '"
                + (runFullAzdUp ? "azd up" : "azd deploy")
                + " --no-prompt' inside the live "
                + "sandbox session to "
                + (runFullAzdUp ? "provision + " : "")
                + "build + push application images. This is a one-shot "
                + "pass — if it doesn't clear the hollow state, the deploy will be marked Failed.");

            var env = planEnv.ToDictionary(kv => kv.Key, kv => kv.Value);

            var azdSubcmd = runFullAzdUp ? "azd up" : "azd deploy";
            var recoveryCmd =
                "set -e; " +
                "cd /workspace; " +
                "echo '[recovery] azd env list:'; azd env list --output table 2>&1 || true; " +
                $"echo '[recovery] launching {azdSubcmd} --no-prompt'; " +
                $"{azdSubcmd} --no-prompt";

            var result = await docker.RunAsync(
                recoveryCmd, ".",
                env,
                System.Threading.Timeout.InfiniteTimeSpan,
                ct,
                silenceBudget: _opt.StepSilenceBudget);

            if (result.TimedOutBySilence)
            {
                await Log(s, "warn",
                    "Autonomous recovery: 'azd deploy' went silent for >= the silence " +
                    "budget during build. Re-probing to see what landed before the hang.");
                return true;
            }
            if (result.ExitCode != 0)
            {
                await Log(s, "warn",
                    $"Autonomous recovery: 'azd deploy' exited {result.ExitCode}. " +
                    "Re-probing Azure to see whether any Container Apps were updated.");
                return true;
            }

            await Log(s, "info",
                "Autonomous recovery: 'azd deploy' completed successfully. Re-probing Azure...");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Autonomous recovery threw unexpectedly; skipping the re-probe and marking Failed.");
            await Log(s, "warn",
                $"Autonomous recovery aborted ({ex.GetType().Name}: {ex.Message}). " +
                "Falling back to Failed.");
            return false;
        }
    }

    private async Task<IList<string>> ProbeDeployedStateAsync(
        DeploymentSession s, CancellationToken ct)
    {
        var hints = new List<string>();
        try        {
            var rg = ExtractResourceGroupFromLogs(s.Logs);
            if (string.IsNullOrWhiteSpace(rg))
            {
                await Log(s, "info",
                    "Post-deploy probe: could not extract resource-group name from session " +
                    "logs; skipping az containerapp probe (hollow-deploy detection disabled).",
                    null);
                return hints;
            }
            await Log(s, "info",
                $"Post-deploy probe: scanning Container Apps in '{rg}' to detect hollow deploys.",
                null);

            var stdout = new System.Text.StringBuilder();
            var result = await CliWrap.Cli.Wrap("az")
                .WithArguments(new[]
                {
                    "containerapp", "list",
                    "-g", rg,
                    "--query",
                    "[].{name:name, image:properties.template.containers[0].image}",
                    "-o", "json"
                })
                .WithValidation(CliWrap.CommandResultValidation.None)
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(CliWrap.PipeTarget.Null)
                .ExecuteAsync(ct);

            if (result.ExitCode != 0 || stdout.Length < 2)
            {
                await Log(s, "info",
                    $"Post-deploy probe: 'az containerapp list -g {rg}' exited " +
                    $"{result.ExitCode} with {stdout.Length}-byte stdout; treating as " +
                    "indeterminate and skipping hollow-deploy detection.",
                    null);
                return hints;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return hints;

            int total = 0, placeholder = 0;
            var placeholderNames = new List<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                total++;
                var img = e.TryGetProperty("image", out var iEl) &&
                          iEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? iEl.GetString() ?? ""
                    : "";
                if (img.Contains("containerapps-helloworld", StringComparison.OrdinalIgnoreCase))
                {
                    placeholder++;
                    if (e.TryGetProperty("name", out var nEl))
                        placeholderNames.Add(nEl.GetString() ?? "");
                }
            }

            if (total > 0 && placeholder > 0)
            {
                var names = string.Join(",", placeholderNames.Take(6));
                hints.Add(
                    $"hollow_deploy:{placeholder}/{total} Container Apps in '{rg}' still running " +
                    $"placeholder image (containerapps-helloworld). Apps: {names}. " +
                    "ACR build+push did not complete — 'azd package' and 'azd deploy' were " +
                    "skipped or failed.");
                await Log(s, "info",
                    $"Post-deploy probe: {placeholder}/{total} Container Apps in '{rg}' are on " +
                    "the Microsoft placeholder image (real app images never built/pushed).",
                    null);
            }
            else if (total == 0)
            {
                // No Container Apps at all — provision either never ran
                // or rolled back. azd deploy on its own is useless here:
                // there are no CAs to push images to. Recovery must
                // re-run provision (azd up). We emit a distinct
                // ':no_cas' suffix so the recovery path can pick the
                // right command.
                hints.Add(
                    $"hollow_deploy:no_cas:0 Container Apps in '{rg}'. Infrastructure " +
                    "provisioning never produced any Container Apps; 'azd deploy' on its " +
                    "own cannot recover. Recovery must re-run 'azd up' to provision + " +
                    "deploy from scratch.");
                await Log(s, "info",
                    $"Post-deploy probe: 0 Container Apps in '{rg}'. Provision did not " +
                    "materialise any CAs; flagging for autonomous 'azd up' recovery pass.",
                    null);
            }
            else if (total > 0)
            {
                await Log(s, "info",
                    $"Post-deploy probe: {total} Container Apps in '{rg}' are serving real " +
                    "images (not the placeholder).", null);
            }
        }
        catch (Exception ex)
        {
            // Probe failures are non-fatal — never block the deploy over
            // diagnostics infrastructure. Log and move on.
            await Log(s, "info",
                $"Post-deploy probe skipped (non-fatal): {ex.GetType().Name} {ex.Message}",
                null);
        }
        return hints;
    }

    /// <summary>
    /// Sift through the session's log tail for the name of the Azure
    /// Resource Group the deploy targeted. azd prints this in multiple
    /// places — the 'azd env get-values' output, the provision summary,
    /// error messages referencing "resource group: rg-xyz". We prefer
    /// the most recent mention so cross-attempt noise from an earlier
    /// 'azd down' doesn't confuse us.
    /// </summary>
    private static string? ExtractResourceGroupFromLogs(
        IReadOnlyList<AgentStationHub.Models.LogEntry> logs)
    {
        // Pattern 1: 'AZURE_RESOURCE_GROUP=rg-xyz' (from azd env dump)
        // Pattern 2: '"resourceGroup": "rg-xyz"' (from az json output)
        // Pattern 3: 'resource group: rg-xyz' (from azd error messages)
        // Pattern 4: 'az group delete -n rg-xyz' (from Doctor remediations)
        // Require at least 3 characters after the prefix to avoid matching
        // placeholder-like 'rg-' alone.
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex(
                @"AZURE_RESOURCE_GROUP[=:]\s*""?([A-Za-z0-9_-]{3,64})""?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new System.Text.RegularExpressions.Regex(
                @"""resourceGroup""\s*:\s*""([A-Za-z0-9_-]{3,64})""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new System.Text.RegularExpressions.Regex(
                @"resource group[:\s]+([A-Za-z0-9_-]{3,64})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new System.Text.RegularExpressions.Regex(
                @"az\s+group\s+\w+\s+(?:-n|--name)\s+([A-Za-z0-9_-]{3,64})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        };

        for (int i = logs.Count - 1; i >= 0 && i >= logs.Count - 500; i--)
        {
            var line = logs[i].Message ?? "";
            foreach (var rx in patterns)
            {
                var m = rx.Match(line);
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    // Filter obvious false positives.
                    if (name.Length < 3) continue;
                    if (name.Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
                    return name;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// When the Doctor returns a 'give_up' (typically with `[Escalate]`)
    /// for a failure class that has a deterministic in-sandbox fix, the
    /// orchestrator can synthesise the remediation itself rather than
    /// bouncing the user out to "open a PR on the upstream repo". The
    /// guarded patterns are conservative — only fire when the error
    /// reasoning + step tail clearly identify the issue and a known-good
    /// substitution exists.
    ///
    /// Currently handled:
    ///   • Deprecated OpenAI model names in Bicep / parameters / yaml.
    ///     The hosted Doctor escalates with messages like
    ///     "deploying the deprecated OpenAI model 'gpt-4o-realtime-preview'".
    ///     The fix is mechanical: sed -i across .bicep / .json / .yaml
    ///     files in the workspace, then re-run the failing step.
    /// </summary>
    private static AgentStationHub.Models.Remediation?
        TryAutoPatchEscalation(string reasoning, string stepTail, int failingStepId, string failingCommand)
    {
        var blob = (reasoning ?? string.Empty) + "\n" + (stepTail ?? string.Empty);
        if (blob.Length == 0) return null;

        // -----------------------------------------------------------------
        // Pattern A: ContainerAppSecretInvalid for empty secret values.
        // ACA rejects `--parameters foo=''` when foo is wired into a
        // Container App secret. The classical signature on `azd up`
        // bypassed via `az deployment sub/group create` is:
        //   "Container app secret(s) with name(s) 'a, b, c' are invalid:
        //    value or keyVaultUrl and identity should be provided."
        // The deterministic fix is to strip the empty `=''` parameters
        // from the failing command and re-run; the Bicep modules
        // typically have `@secure() string foo = ''` defaults so dropping
        // the parameter altogether satisfies validation.
        // -----------------------------------------------------------------
        if (blob.Contains("ContainerAppSecretInvalid", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(failingCommand)
            && System.Text.RegularExpressions.Regex.IsMatch(failingCommand, @"=''|=\""\""")
            && failingCommand.Contains("az deployment", StringComparison.OrdinalIgnoreCase))
        {
            // Drop every `name=''` (or `name=""`) token from the command.
            // The token shape is `[A-Za-z_][A-Za-z0-9_]*=(''|"")` and is
            // typically space-separated after `--parameters`.
            var rewritten = System.Text.RegularExpressions.Regex.Replace(
                failingCommand,
                @"\s+[A-Za-z_][A-Za-z0-9_]*=(?:''|"""")",
                "");
            // If after stripping every empty kv pair the `--parameters`
            // flag is now bare or trailed by another flag, leave it; az
            // tolerates an empty `--parameters` list.
            if (!string.Equals(rewritten, failingCommand, StringComparison.Ordinal))
            {
                var newStepA = new AgentStationHub.Models.DeploymentStep(
                    Id: 0,
                    Description:
                        "[Auto-patch] re-run deployment without empty Container App secret parameters " +
                        "(synthesised after Doctor escalation on ContainerAppSecretInvalid)",
                    Command: rewritten,
                    WorkingDirectory: ".");
                return new AgentStationHub.Models.Remediation(
                    Kind: "replace_step",
                    StepId: failingStepId,
                    NewSteps: new[] { newStepA },
                    Reasoning:
                        "[auto-patched escalation] ACA rejected the deployment because four secret " +
                        "parameters were passed as empty strings. Stripped the empty `=''` arguments " +
                        "and re-issued the deployment so the Bicep `@secure()` defaults take over.");
            }
        }

        // -----------------------------------------------------------------
        // Pattern B: InvalidPrincipalId on role-assignment sub-deployments.
        // Bicep templates that ship `roleAssignments.bicep` for storage /
        // Key Vault grants need a non-empty `principalId`. azd normally
        // populates it from `azd env get-values AZURE_PRINCIPAL_ID`; when
        // the Strategist bypasses azd it forgets to set it, and ARM
        // returns "A valid principal ID must be provided for role
        // assignment.". The fix wraps the failing `az deployment` call so
        // it first resolves PRINCIPAL_ID (signed-in user, falling back to
        // the SP behind AZURE_CLIENT_ID), then re-runs the same command
        // with `principalId=$PID` appended to `--parameters`.
        // -----------------------------------------------------------------
        if (blob.Contains("InvalidPrincipalId", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(failingCommand)
            && failingCommand.Contains("az deployment", StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(
                   failingCommand, @"\bprincipalId="))
        {
            // Wrap the original command in a bash chain that resolves
            // PRINCIPAL_ID. We use `bash -lc` and inject the original
            // command literally; we must escape single quotes inside it.
            var inner = failingCommand
                .Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var wrapped =
                "bash -lc 'set -e; " +
                "PID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null " +
                "|| (test -n \"$AZURE_CLIENT_ID\" && az ad sp show --id \"$AZURE_CLIENT_ID\" --query id -o tsv)); " +
                "if [ -z \"$PID\" ]; then echo \"[auto-patch] could not resolve principalId\" >&2; exit 1; fi; " +
                "echo \"[auto-patch] principalId=$PID\"; " +
                $"{inner} principalId=$PID'";

            var newStepB = new AgentStationHub.Models.DeploymentStep(
                Id: 0,
                Description:
                    "[Auto-patch] resolve current principalId and re-run deployment with it appended " +
                    "to --parameters (synthesised after Doctor escalation on InvalidPrincipalId)",
                Command: wrapped,
                WorkingDirectory: ".");
            return new AgentStationHub.Models.Remediation(
                Kind: "replace_step",
                StepId: failingStepId,
                NewSteps: new[] { newStepB },
                Reasoning:
                    "[auto-patched escalation] role-assignment sub-deployment failed with " +
                    "InvalidPrincipalId because principalId was missing from the parameters. " +
                    "Wrapped the original command to resolve principalId from the signed-in user " +
                    "(fallback: the AZURE_CLIENT_ID service principal) and append it.");
        }

        // -----------------------------------------------------------------
        // Pattern D: InvalidResourceLocation cross-region collision.
        // ARM signature:
        //   "The resource '<name>' already exists in location '<existingRegion>'
        //    in resource group '<rg>'. A resource with the same name cannot
        //    be created in location '<newRegion>'."
        // Cause: a previous deploy of the same repo (same AZURE_ENV_NAME,
        // hence same deterministic resource names + same RG name) was
        // provisioned in <existingRegion>; the current attempt targets a
        // different region, but `azd` derives RG/identity names from the
        // env name, so ARM rejects it. Region-pinned resources like
        // managed identities, ACR, etc. cannot move.
        // Smallest surgical fix: pivot the current azd env to the
        // existing region and re-run agentic-azd-up. This converges on
        // whatever was already partially provisioned instead of fighting
        // ARM. We deliberately do NOT delete the old RG (destructive,
        // requires explicit human consent).
        // -----------------------------------------------------------------
        if (blob.Contains("InvalidResourceLocation", StringComparison.OrdinalIgnoreCase))
        {
            var locMatch = System.Text.RegularExpressions.Regex.Match(
                blob,
                @"already\s+exists\s+in\s+location\s+'([a-z0-9]+)'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (locMatch.Success)
            {
                var existingRegion = locMatch.Groups[1].Value.ToLowerInvariant();
                var pivot =
                    $"bash -lc 'set -e; cd /workspace; " +
                    $"echo \"[auto-patch] pivoting AZURE_LOCATION to {existingRegion} \" " +
                    $"\"to match the resource group already provisioned by a previous run\"; " +
                    $"azd env set AZURE_LOCATION {existingRegion}; " +
                    $"agentic-azd-up'";
                var newStepD = new AgentStationHub.Models.DeploymentStep(
                    Id: 0,
                    Description:
                        $"[Auto-patch] pivot AZURE_LOCATION to '{existingRegion}' (region of pre-existing RG) " +
                        "and re-run agentic-azd-up (synthesised after Doctor escalation on InvalidResourceLocation)",
                    Command: pivot,
                    WorkingDirectory: "/workspace");
                return new AgentStationHub.Models.Remediation(
                    Kind: "replace_step",
                    StepId: failingStepId,
                    NewSteps: new[] { newStepD },
                    Reasoning:
                        $"[auto-patched escalation] ARM rejected the deployment because a previous run " +
                        $"of the same repo provisioned region-pinned resources (e.g. managed identity) " +
                        $"in '{existingRegion}', and the current attempt targets a different region. " +
                        $"Pivoted azd env to '{existingRegion}' so the deploy converges on the existing RG " +
                        $"instead of fighting ARM over name collisions.");
            }
        }

        // -----------------------------------------------------------------
        // Pattern C: deprecated / unsupported OpenAI model + optional version.
        // Existing logic; preserved verbatim.
        // -----------------------------------------------------------------

        // Trigger words. The Foundry Doctor uses a wide vocabulary for
        // "this model is the wrong choice": deprecated, unsupported, not
        // supported, retired, no longer available, …. All of them imply
        // the same mechanical fix on our side: sed-replace the offending
        // model/version pair with a known-good one.
        if (!System.Text.RegularExpressions.Regex.IsMatch(blob,
                @"deprecat\w*|unsupported|not\s+supported|retired|no\s+longer\s+available",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;

        // Extract model name. Two surfaces:
        //  (a) ARM error envelope: 'Format:OpenAI,Name:gpt-realtime,Version:2024-12-17'
        //      emitted by DeploymentModelNotSupported / ServiceModelDeprecated.
        //      The whole quoted token contains colons and commas so the
        //      generic [A-Za-z0-9\-_.] regex below cannot capture it.
        //  (b) Doctor reasoning + plain ARM text: 'gpt-realtime' alone.
        //
        // We probe (a) first because when both surfaces are present in
        // the same blob (typical: the ARM error is included in the log
        // tail and the Doctor reasoning quotes the bare model name) we
        // want the ARM authoritative pair, not the bare token.
        string? modelName = null;
        string? badVersion = null;

        var armMatch = System.Text.RegularExpressions.Regex.Match(
            blob,
            @"Name:\s*([A-Za-z0-9][A-Za-z0-9\-_.]{2,80})\s*,\s*Version:\s*(\d{4}-\d{2}-\d{2})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (armMatch.Success)
        {
            modelName = armMatch.Groups[1].Value;
            badVersion = armMatch.Groups[2].Value;
        }

        // (b) Quoted token fallback. Sanity-check it looks like an OpenAI
        // model id so we don't accidentally rewrite a sample app name.
        if (modelName is null)
        {
            foreach (System.Text.RegularExpressions.Match mm in
                     System.Text.RegularExpressions.Regex.Matches(
                         blob,
                         @"['""]([A-Za-z0-9][A-Za-z0-9\-_.]{2,80})['""]"))
            {
                var cand = mm.Groups[1].Value;
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        cand,
                        @"^(gpt-|text-|whisper|tts|dall-e|o\d|chatgpt-)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    modelName = cand;
                    break;
                }
            }
        }
        if (modelName is null) return null;

        // Extract version (YYYY-MM-DD) when the Doctor mentions one.
        // Skipped if (a) already populated badVersion from the ARM pair.
        // Pattern: "version '2024-12-17'", "version 2024-12-17", "v 2024-12-17".
        if (badVersion is null)
        {
            var vm = System.Text.RegularExpressions.Regex.Match(
                blob,
                @"version\s+['""]?(\d{4}-\d{2}-\d{2})['""]?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (vm.Success) badVersion = vm.Groups[1].Value;
        }

        // Model-name replacement table (Apr 2026): conservative set
        // observed working in eastus2 / swedencentral / westeurope.
        string? modelReplacement = modelName.ToLowerInvariant() switch
        {
            "gpt-4o-realtime-preview"      => "gpt-realtime",
            "gpt-4o-mini-realtime-preview" => "gpt-realtime",
            "gpt-4-turbo-preview"          => "gpt-4o-mini",
            "gpt-4-turbo"                  => "gpt-4o-mini",
            "gpt-4-32k"                    => "gpt-4o-mini",
            "gpt-4"                        => "gpt-4o-mini",
            "gpt-35-turbo"                 => "gpt-4o-mini",
            "gpt-35-turbo-16k"             => "gpt-4o-mini",
            "gpt-3.5-turbo"                => "gpt-4o-mini",
            "gpt-3.5-turbo-16k"            => "gpt-4o-mini",
            "text-davinci-003"             => "gpt-4o-mini",
            "text-davinci-002"             => "gpt-4o-mini",
            _ => null
        };

        // (model, badVersion) -> goodVersion table. Used when the model
        // itself is fine but the pinned version is no longer available.
        // Keys are normalised to lower-case + the version string.
        string? versionReplacement = null;
        if (badVersion is not null)
        {
            versionReplacement = (modelName.ToLowerInvariant(), badVersion) switch
            {
                ("gpt-realtime",                 "2024-12-17") => "2025-08-28",
                ("gpt-realtime",                 "2024-10-01") => "2025-08-28",
                // The deprecated gpt-4o-realtime-preview never existed at
                // 2025-08-28; but when our own auto-patch swaps the name
                // to gpt-realtime the version must move too. We also
                // pre-emptively handle the case where the Doctor's first
                // escalation already mentions both the deprecated name
                // and the unsupported version: applying both swaps in a
                // single pass avoids the ping-pong loop where the
                // Doctor reverts the name and the version gets stuck.
                ("gpt-4o-realtime-preview",      "2024-12-17") => "2025-08-28",
                ("gpt-4o-realtime-preview",      "2024-10-01") => "2025-08-28",
                ("gpt-4o-mini-realtime-preview", "2024-12-17") => "2025-08-28",
                ("gpt-4o-mini-realtime-preview", "2024-10-01") => "2025-08-28",
                ("gpt-4o",                       "2024-05-13") => "2024-11-20",
                ("gpt-4o",                       "2024-08-06") => "2024-11-20",
                ("gpt-4o-mini",                  "2024-07-18") => "2024-07-18", // still GA
                _ => null
            };
            if (string.Equals(versionReplacement, badVersion, StringComparison.Ordinal))
                versionReplacement = null;
        }

        // If we have neither a model swap nor a version swap, bail.
        var willSwapModel   = modelReplacement is not null
                               && !string.Equals(modelName, modelReplacement, StringComparison.OrdinalIgnoreCase);
        var willSwapVersion = versionReplacement is not null;
        if (!willSwapModel && !willSwapVersion) return null;

        // Build the bash patch. The whole script is wrapped in `bash -lc
        // '...'` (single-quoted) so we don't have to escape inner double
        // quotes. The model/version tokens are validated above to be
        // [A-Za-z0-9._-]+ / YYYY-MM-DD so they cannot break the quoting.
        var sb = new System.Text.StringBuilder();
        sb.Append("set -e; cd /workspace; ");
        sb.Append("echo \"[auto-patch] Doctor escalation auto-fix:\"; ");

        // Common file scope: every IaC / config file we expect to hold
        // model references.
        const string scope =
            "--include=\"*.bicep\" --include=\"*.bicepparam\" " +
            "--include=\"*.json\" --include=\"*.yaml\" --include=\"*.yml\" " +
            "--include=\"*.env\" --include=\"*.parameters.json\"";

        var description = new System.Text.StringBuilder("[Auto-patch] ");

        if (willSwapModel)
        {
            var oldModel = modelName!;
            var newModel = modelReplacement!;
            sb.Append($"echo \" - model {oldModel} -> {newModel}\"; ");
            sb.Append("files=$(grep -rl ").Append(scope)
              .Append($" -e \"{oldModel}\" . 2>/dev/null || true); ");
            sb.Append("if [ -n \"$files\" ]; then echo \"$files\" | xargs sed -i ")
              .Append($"\"s|{System.Text.RegularExpressions.Regex.Escape(oldModel).Replace("|", "\\|")}|{newModel}|g\"; fi; ");
            description.Append($"replace OpenAI model '{oldModel}' with '{newModel}'");
        }

        if (willSwapVersion)
        {
            var oldV = badVersion!;
            var newV = versionReplacement!;
            sb.Append($"echo \" - version {oldV} -> {newV} (only on lines mentioning 'version')\"; ");
            // Restrict the sed to lines containing 'version' (case-insensitive)
            // to avoid clobbering unrelated dates in the repo.
            sb.Append("vfiles=$(grep -rl ").Append(scope)
              .Append($" -e \"{oldV}\" . 2>/dev/null || true); ");
            sb.Append("if [ -n \"$vfiles\" ]; then echo \"$vfiles\" | xargs sed -i -E ")
              .Append($"\"/version/I s|{oldV}|{newV}|g\"; fi; ");
            if (description.Length > "[Auto-patch] ".Length) description.Append(" + ");
            description.Append($"replace OpenAI model version '{oldV}' with '{newV}'");
        }

        sb.Append("echo \"[auto-patch] done\"");

        var newStep = new AgentStationHub.Models.DeploymentStep(
            Id: 0,
            Description: description.ToString() + " (synthesised after Doctor escalation)",
            Command: $"bash -lc '{sb}'",
            WorkingDirectory: "/workspace");

        var reasoningSummary = new System.Text.StringBuilder("[auto-patched escalation] ");
        if (willSwapModel)
            reasoningSummary.Append($"model '{modelName}' -> '{modelReplacement}'");
        if (willSwapVersion)
        {
            if (willSwapModel) reasoningSummary.Append(", ");
            reasoningSummary.Append($"version '{badVersion}' -> '{versionReplacement}'");
        }
        reasoningSummary.Append(". Synthesised sed across IaC files so the deploy can proceed without a source-repo PR.");

        return new AgentStationHub.Models.Remediation(
            Kind: "insert_before",
            StepId: failingStepId,
            NewSteps: new[] { newStep },
            Reasoning: reasoningSummary.ToString());
    }

    /// <summary>
    /// Extracts a short, stable error signature from a step's tail log so
    /// the DeploymentDoctor can detect when it keeps fighting the same
    /// class of failure across attempts and should escalate to 'give_up'.
    /// Recognises a handful of well-known Azure / azd error codes; falls
    /// back to a sanitized first-200-char snippet otherwise.
    /// </summary>
    private static string SummariseErrorSignature(string tail)
    {
        if (string.IsNullOrWhiteSpace(tail)) return "unknown";
        var knownCodes = new[]
        {
            "ServiceModelDeprecated",
            "DeploymentModelNotSupported",
            "InvalidTemplate",
            "InvalidTemplateDeployment",
            "InvalidResourceLocation",
            "InsufficientResourcesAvailable",
            "QuotaExceeded",
            "NameConflict",
            "SubscriptionQuotaExceeded",
            "ResourceNotFound",
            "AuthenticationFailed",
            "ModuleNotFoundError",
            "cannot execute: required file not found"
        };
        foreach (var code in knownCodes)
        {
            if (tail.Contains(code, StringComparison.OrdinalIgnoreCase))
                return code;
        }

        // Structured synthetic signatures for classes of failure that
        // don't have a tidy Azure error code but are still distinct and
        // recurrent. The circuit breaker needs a STABLE string for each
        // class, otherwise "same failure, different slice of log tail"
        // produces a new signature every time and never trips.
        var lower = tail.ToLowerInvariant();
        if (lower.Contains("no .net sdks were found")
         || lower.Contains("a compatible .net sdk was not found")
         || lower.Contains("the application 'user-secrets' does not exist"))
            return "DotnetSdkNotFound";

        if (lower.Contains("neither docker nor podman is installed")
         || lower.Contains("no container runtime (docker/podman) is installed")
         || lower.Contains("cannot connect to the docker daemon"))
            return "DockerRuntimeMissing";

        // Silence-detector aborted this step — we synthesise a distinct
        // signature so the Doctor knows this is a HANG (no exit code,
        // just no output for N minutes), NOT a crash. The canonical fix
        // for this class on 'docker buildx build' / 'azd deploy' hangs
        // is to switch the affected service to 'az acr build' remote
        // (build on Azure infra, bypassing the fragile local buildkit).
        if (lower.Contains("step produced no output for")
         || lower.Contains("treating as a hang"))
            return "StepSilent";

        // Process killed by the host OOM killer. Classic case: azd's
        // credential chain shells out to 'az account get-access-token'
        // and the Python az process peaks at ~1 GB; when combined with
        // azd, buildx, the Bicep graph and the JSON parsing of a rich
        // subscription scope template, the sandbox cgroup hits its
        // memory cap and Docker Desktop kills the heaviest process —
        // usually az. The error text reported back by azd is
        // "AzureCLICredential: signal: killed". Distinct signature so
        // the Doctor re-tries the same step after a short cool-off;
        // this is a TRANSIENT OOM, not a logic error, and has a >90 %
        // success rate on retry now that we also bumped the sandbox
        // memory cap to 6 GB.
        if (lower.Contains("signal: killed")
         || lower.Contains("signal:killed")
         || (lower.Contains("azureclicredential") && lower.Contains("killed")))
            return "SignalKilled";

        // azd deploy can't find a Container App tagged for a service
        // declared in azure.yaml, AND azd provision has been skipping
        // "no changes detected". This means the local .azure state is
        // out of sync with Azure: azd thinks the infra is already
        // deployed (so provision skips) but Azure has no tagged
        // resources (so deploy fails). Distinct signature so the
        // Doctor goes directly to the 'force Bicep via az deployment
        // sub create' recovery instead of looping on delete-RG +
        // provision + deploy (which never works because azd keeps
        // skipping).
        if (lower.Contains("unable to find a resource tagged with")
         || lower.Contains("azd-service-name")
         || (lower.Contains("didn't find new changes")
             && lower.Contains("azd-service-name")))
            return "AzdStateStale";

        // Azure Container Apps 'Validation timed out' error — Azure's
        // control plane couldn't validate the Container App revision
        // within its internal budget, usually under contention when
        // the Bicep creates many Container Apps in parallel (8-service
        // monorepos hit this regularly). The error is almost always
        // TRANSIENT: Bicep is idempotent, re-running the exact same
        // deployment retries only the failed Container Apps, and by
        // the time the retry fires the regional control plane has
        // capacity again. Distinct signature so the orchestrator can
        // auto-retry (see the matching branch below) without burning
        // a Doctor round-trip on what is effectively a cloud hiccup.
        if (lower.Contains("validation of container app creation")
         || lower.Contains("containerappoperationerror")
         || (lower.Contains("failed: container app:")
             && lower.Contains("timed out")))
            return "ContainerAppValidationTimeout";

        if (lower.Contains("--mount")
         && (lower.Contains("unknown flag") || lower.Contains("dockerfile parse error")
             || lower.Contains("unexpected token")))
            return "BuildKitRequired";

        if (lower.Contains("failed to solve")
         && (lower.Contains("cache") || lower.Contains("mount")))
            return "BuildKitRequired";

        // Noexec bind-mount: Docker Desktop on Windows (WSL2 hosted
        // bind-mount of a Windows drive path) strips the executable
        // bit from files created inside the container. npm extracts
        // esbuild/rollup/etc. into /workspace/**/node_modules/**/bin
        // and the subsequent spawnSync fails EACCES. Same pattern hits
        // bare `./script.sh` calls inside azd hooks. This is distinct
        // from WorkspacePermissionDenied (which is about writes to a
        // read-only path) — the file exists and is readable, just not
        // executable — so the Doctor's canonical fix is different
        // (relocate node_modules to /tmp, not move the operation).
        if ((lower.Contains("eacces") || lower.Contains("permission denied"))
         && lower.Contains("/workspace")
         && (lower.Contains("node_modules")
             || lower.Contains(".sh:")
             || lower.Contains("spawnsync")
             || lower.Contains("esbuild")
             || lower.Contains("/bin/")))
            return "NoexecBindMount";

        // Indirect tells: the Doctor previously rewrote 'npm ci' /
        // 'npm install' with malformed flags and what reaches us is
        // npm's own usage block (no EACCES line, just the help text
        // and "npm error code 1"). Without this branch the failure
        // looks generic and the next Doctor invocation re-attempts
        // another sed-on-npm patch. Tagging it [NoexecBindMount]
        // anyway forces the canonical relocate-to-/tmp recipe.
        if (lower.Contains("npm error")
         && (lower.Contains("aliases: clean-install")
             || lower.Contains("--install-strategy")
             || lower.Contains("run \"npm help ci\"")))
            return "NoexecBindMount";

        if (lower.Contains("permission denied")
         && (lower.Contains("/workspace") || lower.Contains("/.dotnet")))
            return "WorkspacePermissionDenied";

        if (lower.Contains("exec format error"))
            return "ExecFormatError";

        if (lower.Contains("command not found"))
            return "CommandNotFound";

        if (lower.Contains("npm err!") && lower.Contains("enoent"))
            return "NpmEnoent";

        if (lower.Contains("the deployment template contains errors"))
            return "BicepTemplateError";

        // Fallback: first non-empty line after "ERROR:" or the first 80 chars
        var errIdx = tail.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase);
        var slice = errIdx >= 0 ? tail[(errIdx + 6)..] : tail;
        slice = slice.Replace("\r", " ").Replace("\n", " ").Trim();
        return slice.Length > 80 ? slice[..80] + "..." : slice;
    }

    /// <summary>
    /// Re-emits the PlanReady SignalR event with the current (post-
    /// remediation) step list so the UI checklist always matches what the
    /// orchestrator is actually running. Step ids are renumbered so the
    /// checklist shows a clean 1..N sequence after insertions/replacements.
    /// </summary>
    private async Task PublishUpdatedPlanAsync(
        DeploymentSession s, DeploymentPlan plan,
        IList<DeploymentStep> steps, CancellationToken ct)
    {
        var renumbered = steps
            .Select((st, idx) => new DeploymentStep(
                idx + 1, st.Description, st.Command, st.WorkingDirectory, st.Timeout))
            .ToList();
        // Mutate the shared session plan so late-joining clients and the
        // Verifier see the accurate state.
        var updated = plan with { Steps = renumbered };
        s.Plan = updated;
        _store.SaveLater(s);
        await _hub.Clients.Group(s.Id).SendAsync("PlanReady", updated, ct);

        // Also keep the running list coherent with the emitted one so the
        // orchestrator's loop indices line up with what the user sees.
        for (int k = 0; k < steps.Count; k++) steps[k] = renumbered[k];
    }

    /// <summary>
    /// <summary>
    /// Heuristic: does this shell command invoke an azd/az operation that
    /// is likely to run for many minutes with mostly-silent output (Azure
    /// provisioning, packaging, ACR pushes, resource-group teardown)?
    /// If yes, we (a) apply <see cref="DeploymentOptions.LongRunningStepTimeout"/>
    /// instead of the 10-minute default, and (b) attach the progress
    /// watcher so the user sees periodic updates.
    ///
    /// Specifically includes:
    ///   • 'azd up' / 'azd provision' / 'azd deploy' — template provisioning
    ///   • 'az group delete' WITHOUT '--no-wait' — can legitimately take
    ///     30-45 minutes for a group containing OpenAI + Search + Container
    ///     Apps + VNet resources (each with soft-delete + dependency
    ///     teardown chains). Historically the 10-minute default timeout
    ///     killed cleanups mid-way and left half-deleted RGs the next
    ///     deploy had to fight with.
    ///   • 'az cognitiveservices account purge' — often waits on a long
    ///     soft-delete retention period.
    ///   • 'az keyvault purge' — same reason.
    /// </summary>
    private static bool IsLongRunningAzdCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        if (cmd.Contains("azd up",        StringComparison.OrdinalIgnoreCase)
         || cmd.Contains("azd provision", StringComparison.OrdinalIgnoreCase)
         || cmd.Contains("azd deploy",    StringComparison.OrdinalIgnoreCase))
            return true;

        // 'az group delete' without '--no-wait' blocks until Azure confirms
        // every resource has been removed. With '--no-wait' the command
        // returns in <5s, so it doesn't need the extended budget.
        if (cmd.Contains("az group delete", StringComparison.OrdinalIgnoreCase)
            && !cmd.Contains("--no-wait", StringComparison.OrdinalIgnoreCase))
            return true;

        if (cmd.Contains("cognitiveservices account purge", StringComparison.OrdinalIgnoreCase)
         || cmd.Contains("keyvault purge",                  StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the command provisioned (or potentially provisioned)
    /// Azure Container Registry — meaning a follow-up `az acr login` is
    /// worth attempting so subsequent `docker push` / `azd deploy` steps
    /// in the SAME session can authenticate. This deliberately matches both
    /// `azd up` (provision + deploy in one shot) and `azd provision`
    /// (provision only, deploy will run as a separate step).
    /// </summary>
    private static bool TouchesAzdProvision(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        return cmd.Contains("azd up",        StringComparison.OrdinalIgnoreCase)
            || cmd.Contains("azd provision", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looser variant of <see cref="TouchesAzdProvision"/>: matches ANY
    /// invocation of <c>azd</c> (up, provision, deploy, env new, env set,
    /// hooks). Used to decide when to refresh the cached
    /// <c>azd env get-values</c> snapshot via
    /// <see cref="Tools.AzdEnvLoader"/> � essentially every step that
    /// could have created or mutated the azd environment.
    /// </summary>
    private static bool TouchesAnyAzd(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        return cmd.Contains("azd ", StringComparison.OrdinalIgnoreCase)
            || cmd.TrimStart().StartsWith("azd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the step's command appears to need azd-derived
    /// environment values that are NOT yet present in <paramref name="env"/>.
    /// Triggers a pre-step <see cref="AzdEnvLoader.LoadAndMergeAsync"/>
    /// run so commands like
    /// <c>az acr build --registry $AZURE_CONTAINER_REGISTRY_NAME ...</c>
    /// or inline pipelines like
    /// <c>$(azd env get-values | grep AZURE_X | cut -d= -f2)</c>
    /// resolve to the right value even when the step is scheduled
    /// BEFORE the first explicit azd-touching step (a common mistake
    /// the Strategist makes when it pre-emptively switches to ACR
    /// remote build to avoid local docker-build hangs).
    ///
    /// Conservative: returns false if the relevant variable is already
    /// in env (no need to reload), or if the command shows no signs of
    /// needing azd values at all.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AzdRefRegex =
        new(@"\b(?:AZURE|SERVICE|AZD)_[A-Z0-9_]+\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool StepNeedsAzdEnv(
        string cmd, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        // Inline `azd env get-values` pipelines always benefit from a
        // pre-load — that way the substitutor can rewrite them.
        if (cmd.Contains("azd env get-values", StringComparison.Ordinal))
            return true;

        // Direct $AZURE_X / ${AZURE_X} references where the value is
        // not yet in env: pre-load to populate it. If env already has
        // the value (or the variable is something we don't manage),
        // skip the reload.
        var refs = AzdRefRegex.Matches(cmd);
        foreach (System.Text.RegularExpressions.Match m in refs)
        {
            // Skip occurrences inside the `KEY=value` left-hand side
            // (rare, but happens in the Doctor's `REG=$(...)` lines —
            // the right-hand side will already contain the same name).
            if (!env.ContainsKey(m.Value)) return true;
        }
        return false;
    }

    /// <summary>
    /// Best-effort: query <c>azd env get-values</c> for the registry endpoint
    /// of the current azd environment, then run <c>az acr login --name …</c>
    /// inside the sandbox so <c>~/.docker/config.json</c> (persisted in the
    /// SandboxAzureAuth docker-config volume) gets a valid bearer token for
    /// the registry. Subsequent steps that push images then succeed.
    ///
    /// Failure here is intentionally non-fatal: it's a best-effort warm-up.
    /// If the env exposes no ACR (templates that use App Service or
    /// Static Web Apps instead of Container Apps), the helper logs an
    /// informational line and returns. If the login itself fails (network,
    /// RBAC, registry doesn't exist yet because provision was skipped) we
    /// log a warning but still let the main step loop continue — the
    /// subsequent push will fail with a clearer error and the Doctor will
    /// remediate (or the user will see the real reason rather than a
    /// cryptic "denied: authentication required").
    /// </summary>
    private async Task TryEnsureAcrLoginAsync(
        DeploymentSession s,
        DockerShellTool docker,
        DeploymentStep parentStep,
        IReadOnlyDictionary<string, string> env,
        string azdEnvName,
        CancellationToken ct)
    {
        // Single shell snippet: extract the registry endpoint (preferring
        // AZURE_CONTAINER_REGISTRY_ENDPOINT, falling back to the *_NAME
        // variant some templates expose), strip to bare hostname, then
        // login. `set +e` keeps a missing-variable in the env file from
        // turning into a fatal step exit.
        const string acrLoginScript =
            "set +e; " +
            "ACR=$(azd env get-value AZURE_CONTAINER_REGISTRY_ENDPOINT 2>/dev/null); " +
            "if [ -z \"$ACR\" ]; then " +
            "  ACR=$(azd env get-values 2>/dev/null " +
            "        | grep -E '^AZURE_CONTAINER_REGISTRY_(ENDPOINT|NAME)=' " +
            "        | head -1 | cut -d= -f2- | tr -d '\\\"' ); " +
            "fi; " +
            "if [ -z \"$ACR\" ]; then " +
            "  echo '[acr-login-hook] No AZURE_CONTAINER_REGISTRY_* in azd env; nothing to log into.'; " +
            "  exit 0; " +
            "fi; " +
            "ACR_NAME=\"${ACR%%.*}\"; " +
            "echo \"[acr-login-hook] az acr login --name $ACR_NAME (endpoint: $ACR)\"; " +
            "az acr login --name \"$ACR_NAME\" --only-show-errors";

        try
        {
            await Log(s, "info",
                "▶ Post-step hook: ensuring sandbox Docker CLI is logged into the deployment ACR " +
                "so subsequent image pushes authenticate (persisted via the docker-config volume).",
                parentStep.Id);

            // Reuse the parent step's working directory: that's where the
            // azd environment metadata lives (.azure/<env>/.env). 30 s is
            // plenty for the get-values + acr login round trip; if it
            // hangs longer something is wrong with az itself, no point
            // blocking the deploy.
            var hookResult = await docker.RunAsync(
                acrLoginScript,
                parentStep.WorkingDirectory,
                env.ToDictionary(kv => kv.Key, kv => kv.Value),
                TimeSpan.FromSeconds(60),
                ct,
                silenceBudget: TimeSpan.FromSeconds(45));

            if (hookResult.ExitCode != 0)
            {
                await Log(s, "warn",
                    "ACR login hook returned non-zero. Subsequent docker push steps may " +
                    "fail with 'denied: authentication required' — if so, the Doctor will " +
                    "see the auth error and remediate. Tail: " +
                    (string.IsNullOrWhiteSpace(hookResult.TailLog) ? "(empty)" : hookResult.TailLog),
                    parentStep.Id);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await Log(s, "warn",
                $"ACR login hook threw: {ex.GetType().Name}: {ex.Message}. " +
                "Continuing — the subsequent deploy step will fail with a clearer error " +
                "if ACR auth was actually required.",
                parentStep.Id);
        }
    }

    /// <summary>
    /// Locates the azd environment name for this deployment. Priority order:
    /// the planner-supplied env dict, then the argument of the first
    /// 'azd env new &lt;name&gt;' step, then null (watcher simply stays idle).
    /// </summary>
    private static string? ResolveAzdEnvName(
        IReadOnlyDictionary<string, string> env, DeploymentPlan plan)
    {        if (env.TryGetValue("AZURE_ENV_NAME", out var fromEnv) && !string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        foreach (var step in plan.Steps)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                step.Command,
                @"\bazd\s+env\s+new\s+([A-Za-z0-9][A-Za-z0-9_.-]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Rewrites the plan so that the <c>azd env new &lt;name&gt;</c> step
    /// always uses a fresh, time-stamped environment name. azd derives the
    /// target Azure Resource Group as <c>rg-&lt;envName&gt;</c>, so every
    /// run of the same repo would otherwise land in the SAME RG. That's
    /// fine when the previous deploy succeeded, but a failed or cancelled
    /// deploy leaves:
    ///   - the RG in "Deleting" state (new provisioning is blocked until
    ///     the deletion completes, 5-10 min of opaque waiting),
    ///   - soft-deleted Key Vault / Cognitive Services accounts whose name
    ///     now collides with the new provision,
    ///   - Container Apps / revisions in Failed state that the next
    ///     Bicep deployment cannot cleanly reconcile.
    /// Appending a short suffix guarantees a brand-new RG with no stale
    /// siblings. Other steps referencing the old name (e.g. a custom
    /// 'az group delete -n rg-&lt;old&gt;') are rewritten consistently.
    /// Azure env names must stay within 64 chars and match
    /// <c>^[A-Za-z0-9][A-Za-z0-9_.-]*$</c>, so we keep the prefix short.
    /// </summary>
    private static DeploymentPlan EnforceUniqueAzdEnvName(
        DeploymentPlan plan, Action<string> log)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"\bazd\s+env\s+new\s+([A-Za-z0-9][A-Za-z0-9_.-]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Find the first 'azd env new <name>' step; if the planner forgot
        // one the deploy is already broken for other reasons � no-op.
        string? original = null;
        foreach (var step in plan.Steps)
        {
            var m = rx.Match(step.Command);
            if (m.Success) { original = m.Groups[1].Value; break; }
        }
        if (original is null) return plan;

        // Short suffix: yyyyMMdd-HHmm gives minute-level uniqueness,
        // reads like a timestamp in the Azure portal RG list, and keeps
        // the total well under the 64-char azd limit.
        var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");

        // Strip any previous timestamp suffix we might have added so
        // replays of the same plan don't double-stamp.
        var stripRx = new System.Text.RegularExpressions.Regex(@"-\d{8}-\d{4}$");
        var basePrefix = stripRx.Replace(original, "");

        // Cap the prefix so the final string stays <= 40 chars (leaves
        // headroom for azd's internal prefixing into 'rg-' / 'cae-' and
        // for the Bicep-generated resource tokens).
        if (basePrefix.Length > 25) basePrefix = basePrefix[..25].TrimEnd('-', '_', '.');
        var unique = $"{basePrefix}-{suffix}";

        if (string.Equals(unique, original, StringComparison.Ordinal))
            return plan;

        var originalEscaped = System.Text.RegularExpressions.Regex.Escape(original);
        var wholeWordRx = new System.Text.RegularExpressions.Regex(
            $@"(?<![A-Za-z0-9_.-]){originalEscaped}(?![A-Za-z0-9_.-])");

        var rewritten = plan.Steps
            .Select(st => st with { Command = wholeWordRx.Replace(st.Command, unique) })
            .ToList();

        // Also update AZURE_ENV_NAME in the plan env dict (if the planner
        // set it there) so 'azd env refresh' downstream sees the new name.
        var envCopy = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        if (envCopy.TryGetValue("AZURE_ENV_NAME", out var existingEnvName)
            && string.Equals(existingEnvName, original, StringComparison.Ordinal))
        {
            envCopy["AZURE_ENV_NAME"] = unique;
        }

        log($"Enforcing unique azd environment name: '{original}' -> '{unique}' " +
            $"(guarantees a brand-new resource group 'rg-{unique}' per deploy).");

        return plan with
        {
            Steps = rewritten,
            Environment = envCopy
        };
    }

    private static (string? SubscriptionId, string? TenantId) ReadHostDefaultSubscription()
    {
        try
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".azure", "azureProfile.json");
            if (!File.Exists(profilePath)) return (null, null);

            // azureProfile.json has a BOM; strip it before parsing.
            var raw = File.ReadAllText(profilePath).TrimStart('\uFEFF');
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("subscriptions", out var subs))
                return (null, null);

            foreach (var sub in subs.EnumerateArray())
            {
                if (sub.TryGetProperty("isDefault", out var def) && def.GetBoolean())
                {
                    var id = sub.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var tid = sub.TryGetProperty("tenantId", out var tidEl) ? tidEl.GetString() : null;
                    return (id, tid);
                }
            }
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }
}
