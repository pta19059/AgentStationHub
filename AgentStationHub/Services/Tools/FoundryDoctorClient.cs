using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStationHub.Models;
using Azure.Core;
using Azure.Identity;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Calls the Doctor Foundry Hosted Agent (registered as `ash-doctor-hosted`
/// on the Foundry project) over the Invocations protocol. Used by the
/// orchestrator as the SOLE remediation path when the
/// <c>Foundry:UseFoundryDoctor</c> feature flag is on.
///
/// STRICT MODE: returning null DOES NOT trigger the in-sandbox Doctor
/// any more — the orchestrator fails the step explicitly so the
/// operator can fix / redeploy the hosted agent instead of silently
/// degrading to a different model. Toggle <c>Foundry:UseFoundryDoctor</c>
/// off to revert to the in-sandbox Doctor.
/// </summary>
public sealed class FoundryDoctorClient
{
    private static readonly string[] AiAzureScope = new[] { "https://ai.azure.com/.default" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // File-path candidate regex: things like "src/Program.cs",
    // "/workspace/foo/bar.csproj", "Dockerfile". Anchored on whitespace
    // boundaries so tail tokens like "(at line 12)" do not pollute it.
    // Capture supported extensions only � avoids reading binary or
    // generated noise.
    private static readonly Regex PathLikeRx = new(
        @"(?<![A-Za-z0-9_])(?:[A-Za-z0-9_./\\\-]+\.(?:cs|csproj|sln|json|yaml|yml|toml|ts|tsx|js|jsx|py|go|rs|java|kt|gradle|xml|sh|ps1|bicep|tf|md|txt|env|Dockerfile)|Dockerfile|docker-compose\.ya?ml|requirements\.txt|package\.json|pyproject\.toml)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly string _invokeUrl;
    private readonly TokenCredential _cred;
    private readonly bool _useApiKey;
    private readonly string? _apiKey;

    public FoundryDoctorClient(
        HttpClient http,
        string invokeUrl,
        string? tenantId,
        string? apiKey)
    {
        _http = http;
        _invokeUrl = NormalizeInvokeUrl(invokeUrl);
        _cred = string.IsNullOrWhiteSpace(tenantId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
        _apiKey = apiKey;
        _useApiKey = !string.IsNullOrWhiteSpace(apiKey);
    }

    /// <summary>
    /// Canonicalises whatever URL was wired in config into the form the
    /// Foundry data plane actually serves:
    ///   <c>{project}/agents/{name}/endpoint/protocols/invocations?api-version=v1</c>
    /// Accepts inputs like:
    ///   - {project}/agents/{name}
    ///   - {project}/agents/{name}/versions/{n}            (legacy management-plane shape)
    ///   - {project}/agents/{name}/versions/{n}/invocations (legacy buggy shape that returns 500)
    ///   - {project}/agents/{name}/invocations
    ///   - {project}/agents/{name}/endpoint/protocols/invocations
    /// and always emits the v1 invocations endpoint with the
    /// <c>api-version</c> query parameter present (the data plane returns
    /// HTTP 400 "Missing required query parameter: api-version" without
    /// it). Traffic routing across versions is platform-managed; the
    /// caller MUST NOT pin a specific /versions/{n} segment.
    /// </summary>
    internal static string NormalizeInvokeUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        // Strip query/fragment first so we can rebuild it cleanly.
        var qIdx = raw.IndexOf('?');
        var path = qIdx >= 0 ? raw[..qIdx] : raw;
        path = path.TrimEnd('/');

        // Drop trailing /invocations or /endpoint/protocols/invocations
        // so we can re-add the canonical form once.
        if (path.EndsWith("/endpoint/protocols/invocations", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/endpoint/protocols/invocations".Length];
        else if (path.EndsWith("/invocations", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/invocations".Length];

        // Drop /versions/{n} � the platform routes traffic across versions
        // automatically; pinning a specific version on the data-plane
        // invoke URL is unsupported and yields 5xx.
        var verIdx = path.LastIndexOf("/versions/", StringComparison.OrdinalIgnoreCase);
        if (verIdx >= 0)
            path = path[..verIdx];

        return path + "/endpoint/protocols/invocations?api-version=v1";
    }

    public string InvokeUrl => _invokeUrl;

    /// <summary>
    /// Bearer-token-authenticated POST of a remediation request to the hosted
    /// Doctor. Returns null on any failure (bad endpoint, auth failure,
    /// non-OK ok-flag, JSON deserialization error). The orchestrator MUST
    /// treat null as "Foundry path unavailable, fall back to in-sandbox".
    /// </summary>
    public async Task<Remediation?> RemediateAsync(
        string hostWorkspace,
        DeploymentPlan plan,
        int failedStepId,
        string errorTail,
        IReadOnlyList<string> previousAttempts,
        Action<string, string> onLog,
        CancellationToken ct,
        IReadOnlyList<AgentInsight>? priorInsights = null)
    {
        try
        {
            // Pre-bundle repo file slices � the hosted Doctor cannot read
            // the host /workspace volume, so we ship the most likely
            // relevant files in the request body itself.
            var repoFiles = TryBundleFiles(hostWorkspace, plan, errorTail, onLog);

            var body = new DoctorWireRequest
            {
                Command = "remediate",
                Workspace = "/workspace",
                FailedStepId = failedStepId,
                ErrorTail = errorTail,
                PreviousAttempts = previousAttempts.ToList(),
                PriorInsights = priorInsights?
                    .Select(i => new WirePriorInsight
                    {
                        Key = i.Key, Value = i.Value,
                        Confidence = i.Confidence, At = i.At.ToString("o")
                    })
                    .ToList(),
                RepoFiles = repoFiles,
                Plan = new WirePlan
                {
                    Prerequisites = plan.Prerequisites.ToList(),
                    Env = plan.Environment.ToDictionary(kv => kv.Key, kv => kv.Value),
                    VerifyHints = plan.VerifyHints.ToList(),
                    RepoKind = plan.RepoKind,
                    Steps = plan.Steps
                        .Select(s => new WireStep
                        {
                            Id = s.Id,
                            Description = s.Description,
                            Cmd = s.Command,
                            Cwd = s.WorkingDirectory,
                            ActionJson = s.ActionJson
                        })
                        .ToList()
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _invokeUrl)
            {
                Content = JsonContent.Create(body, options: JsonOpts)
            };

            // Required preview opt-in header for the hosted-agent data plane.
            // Without it the server returns HTTP 403 preview_feature_required.
            req.Headers.Add("Foundry-Features", "HostedAgents=V1Preview");

            if (_useApiKey)
            {
                req.Headers.Add("api-key", _apiKey);
            }
            else
            {
                var token = await _cred.GetTokenAsync(
                    new TokenRequestContext(AiAzureScope), ct);
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.Token);
            }

            onLog("info",
                $"[Foundry] calling hosted Doctor at {_invokeUrl} " +
                $"(repoFiles={repoFiles?.Count ?? 0})");

            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                onLog("err",
                    $"[Foundry] hosted Doctor HTTP {(int)resp.StatusCode}: " +
                    Truncate(raw, 400));
                return null;
            }

            var wire = JsonSerializer.Deserialize<DoctorWireResponse>(raw, JsonOpts);
            if (wire is null || !wire.Ok || wire.Remediation is null)
            {
                onLog("err",
                    $"[Foundry] hosted Doctor returned no remediation: " +
                    (wire?.Error ?? "(empty)"));
                return null;
            }

            // Mirror trace lines back into the live log so the user sees
            // the Doctor's reasoning even though it ran in Foundry.
            if (wire.Trace is not null)
            {
                foreach (var t in wire.Trace)
                {
                    onLog("info", $"[Foundry/{t.Agent}/{t.Stage}] {t.Message}");
                }
            }

            var newSteps = (wire.Remediation.NewSteps ?? new List<WireStep>())
                .Select(s => new DeploymentStep(s.Id, s.Description, s.Cmd, s.Cwd)
                {
                    ActionJson = s.ActionJson
                })
                .ToList();

            return new Remediation(
                wire.Remediation.Kind ?? "give_up",
                wire.Remediation.StepId,
                newSteps,
                wire.Remediation.Reasoning);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onLog("err", $"[Foundry] hosted Doctor call threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds a small map of (relativePath -> truncated content) covering
    /// files referenced by the failing step's command, by the error tail,
    /// and by a handful of well-known config files. Cap: 8 files * 64 KB.
    /// </summary>
    private static Dictionary<string, string>? TryBundleFiles(
        string hostWorkspace,
        DeploymentPlan plan,
        string errorTail,
        Action<string, string> onLog)
    {
        const int MaxFiles = 8;
        const int MaxBytesPerFile = 64 * 1024;

        if (string.IsNullOrWhiteSpace(hostWorkspace) || !Directory.Exists(hostWorkspace))
            return null;

        // Candidate set: extract path-like tokens from errorTail + the
        // failing step's cmd + a few canonical config files.
        var candidates = new List<string>();

        void AddMatches(string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            foreach (Match m in PathLikeRx.Matches(source))
            {
                var p = m.Value.Replace("\\", "/").TrimStart('.', '/');
                if (p.Length > 0 && !candidates.Contains(p))
                    candidates.Add(p);
            }
        }

        AddMatches(errorTail);
        foreach (var s in plan.Steps) AddMatches(s.Command);

        // Canonical roots � cheap and high signal.
        foreach (var p in new[]
        {
            "Dockerfile", "docker-compose.yml", "docker-compose.yaml",
            "package.json", "requirements.txt", "pyproject.toml",
            "azure.yaml", "main.bicep", "infra/main.bicep",
            "appsettings.json"
        })
        {
            if (!candidates.Contains(p)) candidates.Add(p);
        }

        var bundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in candidates)
        {
            if (bundle.Count >= MaxFiles) break;
            // Guard against directory traversal: resolve and verify the
            // result stays under hostWorkspace.
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(hostWorkspace, rel));
            }
            catch { continue; }
            var rootFull = Path.GetFullPath(hostWorkspace);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;

            try
            {
                var info = new FileInfo(full);
                if (info.Length == 0) continue;
                var bytesToRead = (int)Math.Min(info.Length, MaxBytesPerFile);
                using var fs = File.OpenRead(full);
                var buf = new byte[bytesToRead];
                var read = fs.Read(buf, 0, bytesToRead);
                var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                bundle[rel] = text;
            }
            catch (Exception ex)
            {
                onLog("info", $"[Foundry] skip bundle {rel}: {ex.Message}");
            }
        }

        return bundle.Count == 0 ? null : bundle;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    // --- Wire DTOs (must mirror AgentStationHub.DoctorAgent.Contracts.cs) ---

    private sealed class DoctorWireRequest
    {
        [JsonPropertyName("command")]          public string Command { get; set; } = "remediate";
        [JsonPropertyName("workspace")]        public string Workspace { get; set; } = "";
        [JsonPropertyName("plan")]             public WirePlan? Plan { get; set; }
        [JsonPropertyName("failedStepId")]     public int? FailedStepId { get; set; }
        [JsonPropertyName("errorTail")]        public string? ErrorTail { get; set; }
        [JsonPropertyName("previousAttempts")] public List<string>? PreviousAttempts { get; set; }
        [JsonPropertyName("priorInsights")]    public List<WirePriorInsight>? PriorInsights { get; set; }
        [JsonPropertyName("repoFiles")]        public Dictionary<string, string>? RepoFiles { get; set; }
    }

    private sealed class DoctorWireResponse
    {
        [JsonPropertyName("ok")]          public bool Ok { get; set; }
        [JsonPropertyName("error")]       public string? Error { get; set; }
        [JsonPropertyName("remediation")] public WireRemediation? Remediation { get; set; }
        [JsonPropertyName("trace")]       public List<WireTrace>? Trace { get; set; }
    }

    private sealed class WireTrace
    {
        [JsonPropertyName("agent")]   public string Agent { get; set; } = "";
        [JsonPropertyName("stage")]   public string Stage { get; set; } = "";
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    private sealed class WirePlan
    {
        [JsonPropertyName("prerequisites")] public List<string>? Prerequisites { get; set; }
        [JsonPropertyName("env")]           public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("steps")]         public List<WireStep>? Steps { get; set; }
        [JsonPropertyName("verifyHints")]   public List<string>? VerifyHints { get; set; }
        [JsonPropertyName("repoKind")]      public string? RepoKind { get; set; }
    }

    private sealed class WireStep
    {
        [JsonPropertyName("id")]          public int Id { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("cmd")]         public string Cmd { get; set; } = "";
        [JsonPropertyName("cwd")]         public string Cwd { get; set; } = ".";
        [JsonPropertyName("actionJson")]  public string? ActionJson { get; set; }
    }

    private sealed class WireRemediation
    {
        [JsonPropertyName("kind")]      public string? Kind { get; set; }
        [JsonPropertyName("stepId")]    public int StepId { get; set; }
        [JsonPropertyName("newSteps")]  public List<WireStep>? NewSteps { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }

    private sealed class WirePriorInsight
    {
        [JsonPropertyName("key")]        public string Key { get; set; } = "";
        [JsonPropertyName("value")]      public string Value { get; set; } = "";
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("at")]         public string At { get; set; } = "";
    }
}
