namespace AgentStationHub.Services.Actions;

/// <summary>
/// Typed deploy state shared across actions in a single session.
/// Replaces the previous "everything is a bash variable" model with
/// strongly-typed accessors and a free-form key/value bag for the
/// long tail of azd env values.
///
/// ## Lifecycle
/// 1. Created at the start of a session by the orchestrator with the
///    repository URL, working dir, target Azure location, and the
///    initial environment dict (defaults from the plan).
/// 2. <see cref="MergeFromAzdEnv"/> is called after every azd-touching
///    step (same point that <c>AzdEnvLoader</c> runs today). It
///    promotes well-known azd keys into typed properties (e.g.
///    <c>AZURE_CONTAINER_REGISTRY_ENDPOINT</c> -> <see cref="AcrEndpoint"/>)
///    and derives sibling values that azd never exports (e.g. the bare
///    ACR <see cref="AcrName"/>, the resource group from the first
///    <c>AZURE_RESOURCE_*_ID</c>).
/// 3. Actions read typed properties (no string parsing) and write
///    discoveries back via <see cref="WithService"/>.
/// 4. The Doctor receives a JSON snapshot when invoked, so it can pick
///    the right action with confidence rather than re-deriving values
///    from inline bash.
///
/// ## Threading
/// Mutations happen on the orchestrator step-loop thread. Actions
/// execute synchronously from the orchestrator's POV (they may spawn
/// internal tasks but return one <see cref="ActionResult"/>). No
/// concurrent step execution today, so plain mutable fields are safe.
/// </summary>
public sealed class DeployContext
{
    public string SessionId { get; }
    public string RepoUrl { get; }
    public string WorkDir { get; }
    public string AzureLocation { get; set; }

    // ─── azd environment basics ─────────────────────────────────
    public string? AzdEnvName { get; set; }
    public string? SubscriptionId { get; set; }
    public string? TenantId { get; set; }

    // ─── Azure deploy targets (post-provision) ──────────────────
    public string? ResourceGroup { get; set; }
    public string? AcrName { get; set; }
    public string? AcrEndpoint { get; set; }

    // ─── Per-service info (populated by AzdDeploy / AcrBuild) ──
    public Dictionary<string, ServiceInfo> Services { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    // ─── Free-form env (used for variable substitution + merged into docker exec -e) ─
    // The orchestrator owns the canonical env dict and passes it in by
    // reference so a single mutation source-of-truth is shared with
    // AzdEnvLoader, AzdEnvSubstitutor, and DockerShellTool.RunAsync(env).
    public Dictionary<string, string> Env { get; }

    public DeployContext(
        string sessionId, string repoUrl, string workDir,
        string azureLocation, Dictionary<string, string>? initialEnv = null)
    {
        SessionId = sessionId;
        RepoUrl = repoUrl;
        WorkDir = workDir;
        AzureLocation = azureLocation;
        Env = initialEnv ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Promote well-known azd keys from <paramref name="azdEnv"/> into
    /// typed properties and derive sibling values azd never exports.
    /// Idempotent: safe to call after every azd-touching step.
    /// </summary>
    public void MergeFromAzdEnv(IDictionary<string, string> azdEnv)
    {
        if (!ReferenceEquals(azdEnv, Env))
            foreach (var kv in azdEnv) Env[kv.Key] = kv.Value;

        if (azdEnv.TryGetValue("AZURE_SUBSCRIPTION_ID", out var sub) && !string.IsNullOrWhiteSpace(sub))
            SubscriptionId = sub;
        if (azdEnv.TryGetValue("AZURE_TENANT_ID", out var ten) && !string.IsNullOrWhiteSpace(ten))
            TenantId = ten;
        if (azdEnv.TryGetValue("AZURE_ENV_NAME", out var en) && !string.IsNullOrWhiteSpace(en))
            AzdEnvName = en;
        if (azdEnv.TryGetValue("AZURE_LOCATION", out var loc) && !string.IsNullOrWhiteSpace(loc))
            AzureLocation = loc;

        // ACR endpoint -> name. azd exports only AZURE_CONTAINER_REGISTRY_ENDPOINT
        // for templates that use Container Apps. The Doctor and the
        // ACR-based actions need the BARE name. Strip ".azurecr.io".
        if (azdEnv.TryGetValue("AZURE_CONTAINER_REGISTRY_ENDPOINT", out var endpoint)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            AcrEndpoint = endpoint.Trim();
            const string suffix = ".azurecr.io";
            var name = AcrEndpoint;
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
            var schemeIdx = name.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) name = name[(schemeIdx + 3)..];
            if (!string.IsNullOrWhiteSpace(name)) AcrName = name;
        }
        else if (azdEnv.TryGetValue("AZURE_CONTAINER_REGISTRY_NAME", out var bare)
                 && !string.IsNullOrWhiteSpace(bare))
        {
            AcrName = bare;
        }

        // Resource group: encoded inside every AZURE_RESOURCE_*_ID.
        // Pick the first one and parse /resourceGroups/<rg>/...
        if (string.IsNullOrEmpty(ResourceGroup))
        {
            foreach (var kv in azdEnv)
            {
                if (!kv.Key.StartsWith("AZURE_RESOURCE_", StringComparison.Ordinal) ||
                    !kv.Key.EndsWith("_ID", StringComparison.Ordinal)) continue;
                var rg = ExtractResourceGroup(kv.Value);
                if (!string.IsNullOrWhiteSpace(rg)) { ResourceGroup = rg; break; }
            }
        }

        // Per-service container app IDs and image names.
        foreach (var kv in azdEnv)
        {
            if (kv.Key.StartsWith("AZURE_RESOURCE_", StringComparison.Ordinal) &&
                kv.Key.EndsWith("_ID", StringComparison.Ordinal))
            {
                // AZURE_RESOURCE_UI_ANGULAR_ID -> "ui-angular"
                var middle = kv.Key.Substring("AZURE_RESOURCE_".Length);
                middle = middle[..^"_ID".Length];
                var name = middle.Replace('_', '-').ToLowerInvariant();
                if (!Services.TryGetValue(name, out var info)) info = new ServiceInfo(name);
                info.ContainerAppId = kv.Value;
                Services[name] = info;
            }
            else if (kv.Key.StartsWith("SERVICE_", StringComparison.Ordinal) &&
                     kv.Key.EndsWith("_IMAGE_NAME", StringComparison.Ordinal))
            {
                var middle = kv.Key.Substring("SERVICE_".Length);
                middle = middle[..^"_IMAGE_NAME".Length];
                var name = middle.Replace('_', '-').ToLowerInvariant();
                if (!Services.TryGetValue(name, out var info)) info = new ServiceInfo(name);
                info.ImageName = kv.Value;
                Services[name] = info;
            }
            else if (kv.Key.StartsWith("SERVICE_", StringComparison.Ordinal) &&
                     kv.Key.EndsWith("_RESOURCE_EXISTS", StringComparison.Ordinal))
            {
                var middle = kv.Key.Substring("SERVICE_".Length);
                middle = middle[..^"_RESOURCE_EXISTS".Length];
                var name = middle.Replace('_', '-').ToLowerInvariant();
                if (!Services.TryGetValue(name, out var info)) info = new ServiceInfo(name);
                info.ResourceExists =
                    string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase);
                Services[name] = info;
            }
        }
    }

    /// <summary>Idempotent updater for a single service entry.</summary>
    public void WithService(string name, Action<ServiceInfo> mutate)
    {
        var key = name.ToLowerInvariant();
        if (!Services.TryGetValue(key, out var info)) info = new ServiceInfo(key);
        mutate(info);
        Services[key] = info;
    }

    private static string? ExtractResourceGroup(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        const string token = "/resourceGroups/";
        var i = resourceId.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + token.Length;
        var end = resourceId.IndexOf('/', start);
        return end < 0 ? resourceId[start..] : resourceId[start..end];
    }
}

public sealed class ServiceInfo
{
    public string Name { get; }
    public string? ContainerAppId { get; set; }
    public string? ImageName { get; set; }
    public bool ResourceExists { get; set; }
    public string? LastBuiltImageRef { get; set; }
    public string? LastEndpoint { get; set; }

    public ServiceInfo(string name) => Name = name;
}
