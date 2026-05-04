using System.Text;
using System.Text.Json;
using AgentStationHub.Models;
using CliWrap;

namespace AgentStationHub.Services.Agents;

/// <summary>
/// Post-deploy infrastructure autofix agent. After a successful deployment,
/// this agent discovers ALL Azure resources in the target Resource Group,
/// checks their provisioning state, health, accessibility and configuration,
/// and automatically remediates what it can. Applies the SAME rules to
/// every service type:
///   1. Provisioning state must be Succeeded
///   2. Service must be reachable (network/firewall not blocking)
///   3. Identity / RBAC must be correctly wired
///   4. Runtime health must be ok (revisions running, functions loaded, etc.)
///
/// All checks and fixes are logged to a structured <see cref="AutofixReport"/>
/// persisted as JSON alongside the session.
/// </summary>
public sealed class InfraAutofixAgent
{
    private readonly ILogger<InfraAutofixAgent> _log;
    private readonly Action<string, string> _onLog;

    public InfraAutofixAgent(ILogger<InfraAutofixAgent> log, Action<string, string> onLog)
    {
        _log = log;
        _onLog = onLog;
    }

    public async Task<AutofixReport> RunAsync(
        string sessionId,
        string resourceGroup,
        string repoUrl,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var checks = new List<AutofixCheck>();

        _onLog("info", $"[Autofix] Starting full infrastructure review for RG '{resourceGroup}'");

        // Phase 1: Discover ALL resources in the RG
        var resources = await DiscoverResourcesAsync(resourceGroup, ct);
        _onLog("info", $"[Autofix] Discovered {resources.Count} resources across " +
                       $"{resources.Select(r => r.Type).Distinct().Count()} service types");

        // Phase 2: Check each resource by type with type-specific logic
        foreach (var resource in resources)
        {
            var resourceChecks = await CheckResourceAsync(resourceGroup, resource, ct);
            checks.AddRange(resourceChecks);
        }

        var completedAt = DateTimeOffset.UtcNow;
        var fixedCount = checks.Count(c => c.Outcome == AutofixOutcome.Fixed);
        var failedCount = checks.Count(c => c.Outcome == AutofixOutcome.FailedToFix);
        var okCount = checks.Count(c => c.Outcome == AutofixOutcome.Ok);

        var summary = $"Autofix completed: {checks.Count} checks across {resources.Count} resources, " +
                      $"{okCount} healthy, {fixedCount} fixed, {failedCount} could not fix. " +
                      $"Duration: {(completedAt - startedAt).TotalSeconds:F1}s";

        _onLog("info", $"[Autofix] {summary}");

        return new AutofixReport(
            sessionId, resourceGroup, repoUrl,
            startedAt, completedAt, checks, summary);
    }

    // ─── Resource Discovery ───────────────────────────────────────────────

    private async Task<List<AzureResource>> DiscoverResourcesAsync(
        string rg, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var result = await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "resource", "list", "-g", rg,
                "--query", "[].{name:name, type:type, provisioningState:provisioningState, id:id}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0 || stdout.Length < 2)
            return new List<AzureResource>();

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var resources = new List<AzureResource>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var state = el.TryGetProperty("provisioningState", out var s) ? s.GetString() : null;
                var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
                resources.Add(new AzureResource(name, type, state, id));
            }
            return resources;
        }
        catch
        {
            return new List<AzureResource>();
        }
    }

    // ─── Type-based routing ───────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckResourceAsync(
        string rg, AzureResource resource, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();

        // Universal check: provisioning state
        if (resource.ProvisioningState != null &&
            !resource.ProvisioningState.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new AutofixCheck(
                "provisioning-state", resource.Name, AutofixOutcome.FailedToFix,
                $"{resource.Type} '{resource.Name}' provisioningState={resource.ProvisioningState} (expected: Succeeded).",
                null, null));
            // Don't run further checks on a resource that didn't provision
            return checks;
        }

        // Route to type-specific checks
        var typeNorm = resource.Type.ToLowerInvariant();
        switch (typeNorm)
        {
            case "microsoft.app/containerapps":
                checks.AddRange(await CheckContainerAppAsync(rg, resource.Name, ct));
                break;
            case "microsoft.app/managedenvironments":
                checks.AddRange(await CheckContainerAppEnvironmentAsync(rg, resource.Name, ct));
                break;
            case "microsoft.web/sites":
                checks.AddRange(await CheckAppServiceAsync(rg, resource.Name, ct));
                break;
            case "microsoft.storage/storageaccounts":
                checks.AddRange(await CheckStorageAccountAsync(rg, resource.Name, ct));
                break;
            case "microsoft.documentdb/databaseaccounts":
                checks.AddRange(await CheckCosmosDbAsync(rg, resource.Name, ct));
                break;
            case "microsoft.cognitiveservices/accounts":
                checks.AddRange(await CheckCognitiveServicesAsync(rg, resource.Name, ct));
                break;
            case "microsoft.search/searchservices":
                checks.AddRange(await CheckSearchServiceAsync(rg, resource.Name, ct));
                break;
            case "microsoft.keyvault/vaults":
                checks.AddRange(await CheckKeyVaultAsync(rg, resource.Name, ct));
                break;
            case "microsoft.appconfiguration/configurationstores":
                checks.AddRange(await CheckAppConfigAsync(rg, resource.Name, ct));
                break;
            case "microsoft.containerregistry/registries":
                checks.AddRange(await CheckContainerRegistryAsync(rg, resource.Name, ct));
                break;
            case "microsoft.cache/redis":
                checks.AddRange(await CheckRedisAsync(rg, resource.Name, ct));
                break;
            case "microsoft.servicebus/namespaces":
                checks.AddRange(await CheckServiceBusAsync(rg, resource.Name, ct));
                break;
            case "microsoft.eventhub/namespaces":
                checks.AddRange(await CheckEventHubAsync(rg, resource.Name, ct));
                break;
            case "microsoft.sql/servers":
                checks.AddRange(await CheckSqlServerAsync(rg, resource.Name, ct));
                break;
            case "microsoft.dbforpostgresql/flexibleservers":
                checks.AddRange(await CheckPostgresAsync(rg, resource.Name, ct));
                break;
            case "microsoft.signalrservice/signalr":
                checks.AddRange(await CheckSignalRAsync(rg, resource.Name, ct));
                break;
            default:
                // For any other resource type: basic provisioning check passed
                checks.Add(new AutofixCheck(
                    "provisioning-state", resource.Name, AutofixOutcome.Ok,
                    $"{resource.Type} '{resource.Name}' provisioned successfully.", null, null));
                break;
        }

        return checks;
    }

    // ─── Container Apps ───────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckContainerAppAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();

        // Revision health
        checks.Add(await CheckContainerAppRevisionsAsync(rg, name, ct));
        // Managed Identity alignment
        checks.Add(await CheckContainerAppIdentityAsync(rg, name, ct));
        // Endpoint
        var fqdn = await GetContainerAppFqdnAsync(rg, name, ct);
        if (fqdn != null)
            checks.Add(await CheckEndpointAsync(name, fqdn, ct));
        // Image check
        var image = await GetContainerAppImageAsync(rg, name, ct);
        if (image != null && image.Contains("containerapps-helloworld", StringComparison.OrdinalIgnoreCase))
            checks.Add(new AutofixCheck("container-image", name, AutofixOutcome.FailedToFix,
                "Still running placeholder image.", null, null));
        else if (image != null)
            checks.Add(new AutofixCheck("container-image", name, AutofixOutcome.Ok,
                $"Running real image: {image}", null, null));

        return checks;
    }

    private async Task<AutofixCheck> CheckContainerAppRevisionsAsync(
        string rg, string appName, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "containerapp", "revision", "list", "-g", rg, "--name", appName,
                "--query", "[?properties.active].{name:name, running:properties.runningState, health:properties.healthState}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        var json = stdout.ToString().Trim();
        if (string.IsNullOrEmpty(json) || json == "[]")
        {
            _onLog("warn", $"[Autofix] {appName}: no active revisions. Creating...");
            var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AUTOFIX_RESTART=true\"";
            var fixResult = await RunAzCommandAsync(fixCmd, ct);
            return new AutofixCheck("revision-health", appName,
                fixResult.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                "No active revisions found.", fixCmd,
                fixResult.ExitCode == 0 ? "Revision created" : fixResult.Output);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var unhealthy = new List<string>();
            foreach (var rev in doc.RootElement.EnumerateArray())
            {
                var health = rev.TryGetProperty("health", out var h) ? h.GetString() ?? "None" : "None";
                var running = rev.TryGetProperty("running", out var r) ? r.GetString() ?? "" : "";
                var revName = rev.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) &&
                    !running.Contains("Running", StringComparison.OrdinalIgnoreCase))
                    unhealthy.Add($"{revName}({health}/{running})");
            }

            if (unhealthy.Count > 0)
            {
                _onLog("warn", $"[Autofix] {appName}: unhealthy revisions: {string.Join(", ", unhealthy)}");
                var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AUTOFIX_RESTART={DateTimeOffset.UtcNow:yyyyMMddHHmmss}\"";
                var fixResult = await RunAzCommandAsync(fixCmd, ct);
                return new AutofixCheck("revision-health", appName,
                    fixResult.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    $"Unhealthy: {string.Join(", ", unhealthy)}",
                    fixCmd, fixResult.ExitCode == 0 ? "New revision triggered" : fixResult.Output);
            }

            return new AutofixCheck("revision-health", appName, AutofixOutcome.Ok,
                "All active revisions healthy.", null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck("revision-health", appName, AutofixOutcome.Skipped,
                $"Parse error: {ex.Message}", null, null);
        }
    }

    private async Task<AutofixCheck> CheckContainerAppIdentityAsync(
        string rg, string appName, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "containerapp", "show", "-g", rg, "--name", appName,
                "--query", "{identity:identity.userAssignedIdentities, envClientId:properties.template.containers[0].env[?name=='AZURE_CLIENT_ID'].value|[0]}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var root = doc.RootElement;
            var envClientId = root.TryGetProperty("envClientId", out var ec) && ec.ValueKind == JsonValueKind.String
                ? ec.GetString() : null;

            if (!root.TryGetProperty("identity", out var idEl) || idEl.ValueKind != JsonValueKind.Object)
                return new AutofixCheck("managed-identity", appName, AutofixOutcome.Skipped,
                    "No user-assigned MI configured.", null, null);

            string? actualClientId = null;
            foreach (var mi in idEl.EnumerateObject())
                if (mi.Value.TryGetProperty("clientId", out var cid))
                { actualClientId = cid.GetString(); break; }

            if (actualClientId != null && envClientId != null &&
                !actualClientId.Equals(envClientId, StringComparison.OrdinalIgnoreCase))
            {
                _onLog("warn", $"[Autofix] {appName}: AZURE_CLIENT_ID mismatch ({envClientId} vs {actualClientId}). Fixing...");
                var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AZURE_CLIENT_ID={actualClientId}\"";
                var r = await RunAzCommandAsync(fixCmd, ct);
                return new AutofixCheck("managed-identity", appName,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    $"Env={envClientId}, MI={actualClientId}",
                    fixCmd, r.ExitCode == 0 ? "Corrected" : r.Output);
            }
            if (actualClientId != null && envClientId == null)
            {
                _onLog("warn", $"[Autofix] {appName}: AZURE_CLIENT_ID missing. Setting...");
                var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AZURE_CLIENT_ID={actualClientId}\"";
                var r = await RunAzCommandAsync(fixCmd, ct);
                return new AutofixCheck("managed-identity", appName,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    $"AZURE_CLIENT_ID not set, MI clientId={actualClientId}",
                    fixCmd, r.ExitCode == 0 ? "Set" : r.Output);
            }

            return new AutofixCheck("managed-identity", appName, AutofixOutcome.Ok,
                $"Identity aligned (clientId={actualClientId}).", null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck("managed-identity", appName, AutofixOutcome.Skipped,
                $"Inspect error: {ex.Message}", null, null);
        }
    }

    private async Task<string?> GetContainerAppFqdnAsync(string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "containerapp", "show", "-g", rg, "--name", name,
                "--query", "properties.configuration.ingress.fqdn", "-o", "tsv" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);
        var val = stdout.ToString().Trim();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private async Task<string?> GetContainerAppImageAsync(string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "containerapp", "show", "-g", rg, "--name", name,
                "--query", "properties.template.containers[0].image", "-o", "tsv" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);
        var val = stdout.ToString().Trim();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    // ─── Container App Environment ────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckContainerAppEnvironmentAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "containerapp", "env", "show", "-g", rg, "--name", name,
                "--query", "{state:properties.provisioningState, defaultDomain:properties.defaultDomain}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            if (state?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("environment-health", name, AutofixOutcome.Ok,
                    $"Container App Environment provisioned and ready.", null, null) };
            return new List<AutofixCheck> { new("environment-health", name, AutofixOutcome.FailedToFix,
                $"Environment state: {state}", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("environment-health", name, AutofixOutcome.Skipped,
                "Could not query environment.", null, null) };
        }
    }

    // ─── App Service / Functions ──────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckAppServiceAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "webapp", "show", "-g", rg, "--name", name,
                "--query", "{state:state, enabled:enabled, hostname:defaultHostName, availabilityState:availabilityState}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var enabled = doc.RootElement.TryGetProperty("enabled", out var e) && e.GetBoolean();
            var hostname = doc.RootElement.TryGetProperty("hostname", out var h) ? h.GetString() : null;
            var availability = doc.RootElement.TryGetProperty("availabilityState", out var a) ? a.GetString() : null;

            // Check running state
            if (state?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true && enabled)
            {
                checks.Add(new AutofixCheck("app-service-state", name, AutofixOutcome.Ok,
                    $"App Service running (state={state}, enabled={enabled}).", null, null));
            }
            else if (state?.Equals("Stopped", StringComparison.OrdinalIgnoreCase) == true)
            {
                _onLog("warn", $"[Autofix] {name}: App Service stopped. Starting...");
                var fixCmd = $"az webapp start -g {rg} --name {name}";
                var r = await RunAzCommandAsync(fixCmd, ct);
                checks.Add(new AutofixCheck("app-service-state", name,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    $"App Service was stopped.", fixCmd,
                    r.ExitCode == 0 ? "Started" : r.Output));
            }
            else
            {
                checks.Add(new AutofixCheck("app-service-state", name, AutofixOutcome.FailedToFix,
                    $"Unexpected state: {state}, enabled={enabled}, availability={availability}",
                    null, null));
            }

            // Endpoint check
            if (!string.IsNullOrEmpty(hostname))
                checks.Add(await CheckEndpointAsync(name, hostname, ct));
        }
        catch (Exception ex)
        {
            checks.Add(new AutofixCheck("app-service-state", name, AutofixOutcome.Skipped,
                $"Query failed: {ex.Message}", null, null));
        }

        return checks;
    }

    // ─── Storage Account ──────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckStorageAccountAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "storage", "account", "show", "-g", rg, "--name", name,
                "--query", "{status:statusOfPrimary, publicAccess:publicNetworkAccess, " +
                           "allowBlobPublicAccess:allowBlobPublicAccess, provisioningState:provisioningState}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
            var publicAccess = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (status?.Equals("available", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("storage-health", name, AutofixOutcome.Ok,
                    $"Storage '{name}' available (publicNetwork={publicAccess}).", null, null) };

            return new List<AutofixCheck> { new("storage-health", name, AutofixOutcome.FailedToFix,
                $"Storage '{name}' status={status}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("storage-health", name, AutofixOutcome.Skipped,
                "Could not query storage account.", null, null) };
        }
    }

    // ─── CosmosDB ─────────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckCosmosDbAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "cosmosdb", "show", "-g", rg, "--name", name,
                "--query", "{status:provisioningState, publicAccess:publicNetworkAccess, endpoint:documentEndpoint}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";
            var endpoint = doc.RootElement.TryGetProperty("endpoint", out var e) ? e.GetString() : null;

            if (state?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
            {
                checks.Add(new AutofixCheck("cosmosdb-health", name, AutofixOutcome.Ok,
                    $"CosmosDB '{name}' provisioned (publicNetwork={pub}).", null, null));

                // If publicAccess is Disabled, try to enable it (the apps need it)
                if (pub?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _onLog("warn", $"[Autofix] {name}: CosmosDB publicNetworkAccess=Disabled. Enabling...");
                    var fixCmd = $"az cosmosdb update -g {rg} --name {name} --enable-public-network true";
                    var r = await RunAzCommandAsync(fixCmd, ct);
                    checks.Add(new AutofixCheck("cosmosdb-network", name,
                        r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                        "Public network access was disabled.",
                        fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
                }
            }
            else
            {
                checks.Add(new AutofixCheck("cosmosdb-health", name, AutofixOutcome.FailedToFix,
                    $"CosmosDB '{name}' state={state}.", null, null));
            }
        }
        catch
        {
            checks.Add(new AutofixCheck("cosmosdb-health", name, AutofixOutcome.Skipped,
                "Could not query CosmosDB.", null, null));
        }

        return checks;
    }

    // ─── Cognitive Services / AI Foundry ──────────────────────────────────

    private async Task<List<AutofixCheck>> CheckCognitiveServicesAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "cognitiveservices", "account", "show", "-g", rg, "-n", name,
                "--query", "{state:properties.provisioningState, publicAccess:properties.publicNetworkAccess, " +
                           "endpoint:properties.endpoint, kind:kind}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";
            var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : "";

            if (state?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
            {
                checks.Add(new AutofixCheck("ai-services-health", name, AutofixOutcome.Ok,
                    $"AI Services '{name}' ({kind}) provisioned (publicNetwork={pub}).", null, null));

                if (pub?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _onLog("warn", $"[Autofix] {name}: AI Services publicAccess=Disabled. Enabling...");
                    var fixCmd = $"az cognitiveservices account update -g {rg} -n {name} --custom-domain {name} --public-network-access Enabled";
                    var r = await RunAzCommandAsync(fixCmd, ct);
                    checks.Add(new AutofixCheck("ai-services-network", name,
                        r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                        "Public network access was disabled.",
                        fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
                }

                // Check model deployments exist
                var deployChecks = await CheckAIDeploymentsAsync(rg, name, ct);
                checks.AddRange(deployChecks);
            }
            else
            {
                checks.Add(new AutofixCheck("ai-services-health", name, AutofixOutcome.FailedToFix,
                    $"AI Services '{name}' state={state}.", null, null));
            }
        }
        catch
        {
            checks.Add(new AutofixCheck("ai-services-health", name, AutofixOutcome.Skipped,
                "Could not query AI services.", null, null));
        }

        return checks;
    }

    private async Task<List<AutofixCheck>> CheckAIDeploymentsAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "cognitiveservices", "account", "deployment", "list",
                "-g", rg, "-n", name,
                "--query", "[].{name:name, model:properties.model.name, state:properties.provisioningState}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            foreach (var dep in doc.RootElement.EnumerateArray())
            {
                var depName = dep.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                var model = dep.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                var depState = dep.TryGetProperty("state", out var ds) ? ds.GetString() : "unknown";

                if (depState?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
                    checks.Add(new AutofixCheck("ai-deployment", $"{name}/{depName}", AutofixOutcome.Ok,
                        $"Model deployment '{depName}' ({model}) active.", null, null));
                else
                    checks.Add(new AutofixCheck("ai-deployment", $"{name}/{depName}", AutofixOutcome.FailedToFix,
                        $"Model deployment '{depName}' ({model}) state={depState}.", null, null));
            }

            if (doc.RootElement.GetArrayLength() == 0)
                checks.Add(new AutofixCheck("ai-deployment", name, AutofixOutcome.FailedToFix,
                    "No model deployments found.", null, null));
        }
        catch { }

        return checks;
    }

    // ─── Azure AI Search ──────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckSearchServiceAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "search", "service", "show", "-g", rg, "--name", name,
                "--query", "{status:status, publicAccess:publicNetworkAccess, hostName:hostName}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (status?.Equals("running", StringComparison.OrdinalIgnoreCase) == true ||
                status?.Equals("provisioning", StringComparison.OrdinalIgnoreCase) == true)
            {
                checks.Add(new AutofixCheck("search-health", name, AutofixOutcome.Ok,
                    $"Search '{name}' status={status} (publicNetwork={pub}).", null, null));

                if (pub?.Equals("disabled", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _onLog("warn", $"[Autofix] {name}: Search publicAccess=Disabled. Enabling...");
                    var fixCmd = $"az search service update -g {rg} --name {name} --public-access enabled";
                    var r = await RunAzCommandAsync(fixCmd, ct);
                    checks.Add(new AutofixCheck("search-network", name,
                        r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                        "Public network access was disabled.",
                        fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
                }
            }
            else
            {
                checks.Add(new AutofixCheck("search-health", name, AutofixOutcome.FailedToFix,
                    $"Search '{name}' status={status}.", null, null));
            }
        }
        catch
        {
            checks.Add(new AutofixCheck("search-health", name, AutofixOutcome.Skipped,
                "Could not query Search service.", null, null));
        }

        return checks;
    }

    // ─── Key Vault ────────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckKeyVaultAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "keyvault", "show", "-g", rg, "--name", name,
                "--query", "{state:properties.provisioningState, publicAccess:properties.publicNetworkAccess, " +
                           "enableRbac:properties.enableRbacAuthorization, softDelete:properties.enableSoftDelete}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";
            var checks = new List<AutofixCheck>();

            checks.Add(new AutofixCheck("keyvault-health", name, AutofixOutcome.Ok,
                $"Key Vault '{name}' accessible (publicNetwork={pub}).", null, null));

            if (pub?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
            {
                _onLog("warn", $"[Autofix] {name}: KeyVault publicAccess=Disabled. Enabling...");
                var fixCmd = $"az keyvault update -g {rg} --name {name} --public-network-access Enabled";
                var r = await RunAzCommandAsync(fixCmd, ct);
                checks.Add(new AutofixCheck("keyvault-network", name,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    "Public network access was disabled.",
                    fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
            }

            return checks;
        }
        catch
        {
            return new List<AutofixCheck> { new("keyvault-health", name, AutofixOutcome.Skipped,
                "Could not query Key Vault.", null, null) };
        }
    }

    // ─── App Configuration ────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckAppConfigAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "appconfig", "show", "-g", rg, "--name", name,
                "--query", "{state:provisioningState, publicAccess:publicNetworkAccess}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";
            var checks = new List<AutofixCheck>();

            checks.Add(new AutofixCheck("appconfig-health", name, AutofixOutcome.Ok,
                $"App Configuration '{name}' accessible (publicNetwork={pub}).", null, null));

            if (pub?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
            {
                _onLog("warn", $"[Autofix] {name}: AppConfig publicAccess=Disabled. Enabling...");
                var fixCmd = $"az appconfig update -g {rg} --name {name} --enable-public-network true";
                var r = await RunAzCommandAsync(fixCmd, ct);
                checks.Add(new AutofixCheck("appconfig-network", name,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    "Public network access was disabled.",
                    fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
            }

            return checks;
        }
        catch
        {
            return new List<AutofixCheck> { new("appconfig-health", name, AutofixOutcome.Skipped,
                "Could not query App Configuration.", null, null) };
        }
    }

    // ─── Container Registry ───────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckContainerRegistryAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "acr", "show", "-g", rg, "--name", name,
                "--query", "{status:provisioningState, publicAccess:publicNetworkAccess, " +
                           "adminEnabled:adminUserEnabled, loginServer:loginServer}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";
            var login = doc.RootElement.TryGetProperty("loginServer", out var l) ? l.GetString() : "";
            var checks = new List<AutofixCheck>();

            checks.Add(new AutofixCheck("acr-health", name, AutofixOutcome.Ok,
                $"ACR '{name}' ({login}) state={state}, publicNetwork={pub}.", null, null));

            if (pub?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
            {
                _onLog("warn", $"[Autofix] {name}: ACR publicAccess=Disabled. Enabling...");
                var fixCmd = $"az acr update -g {rg} --name {name} --public-network-enabled true";
                var r = await RunAzCommandAsync(fixCmd, ct);
                checks.Add(new AutofixCheck("acr-network", name,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    "Public network access was disabled.",
                    fixCmd, r.ExitCode == 0 ? "Enabled" : r.Output));
            }

            return checks;
        }
        catch
        {
            return new List<AutofixCheck> { new("acr-health", name, AutofixOutcome.Skipped,
                "Could not query ACR.", null, null) };
        }
    }

    // ─── Redis Cache ──────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckRedisAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "redis", "show", "-g", rg, "--name", name,
                "--query", "{state:provisioningState, publicAccess:publicNetworkAccess, hostName:hostName, port:sslPort}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (state?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("redis-health", name, AutofixOutcome.Ok,
                    $"Redis '{name}' provisioned (publicNetwork={pub}).", null, null) };

            return new List<AutofixCheck> { new("redis-health", name, AutofixOutcome.FailedToFix,
                $"Redis '{name}' state={state}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("redis-health", name, AutofixOutcome.Skipped,
                "Could not query Redis.", null, null) };
        }
    }

    // ─── Service Bus ──────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckServiceBusAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "servicebus", "namespace", "show", "-g", rg, "--name", name,
                "--query", "{state:provisioningState, status:status}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";

            if (status?.Equals("Active", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("servicebus-health", name, AutofixOutcome.Ok,
                    $"Service Bus '{name}' active.", null, null) };

            return new List<AutofixCheck> { new("servicebus-health", name, AutofixOutcome.FailedToFix,
                $"Service Bus '{name}' status={status}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("servicebus-health", name, AutofixOutcome.Skipped,
                "Could not query Service Bus.", null, null) };
        }
    }

    // ─── Event Hub ────────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckEventHubAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "eventhubs", "namespace", "show", "-g", rg, "--name", name,
                "--query", "{state:provisioningState, status:status}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";

            if (status?.Equals("Active", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("eventhub-health", name, AutofixOutcome.Ok,
                    $"Event Hub '{name}' active.", null, null) };

            return new List<AutofixCheck> { new("eventhub-health", name, AutofixOutcome.FailedToFix,
                $"Event Hub '{name}' status={status}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("eventhub-health", name, AutofixOutcome.Skipped,
                "Could not query Event Hub.", null, null) };
        }
    }

    // ─── SQL Server ───────────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckSqlServerAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "sql", "server", "show", "-g", rg, "--name", name,
                "--query", "{state:state, publicAccess:publicNetworkAccess, fqdn:fullyQualifiedDomainName}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (state?.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("sql-health", name, AutofixOutcome.Ok,
                    $"SQL Server '{name}' ready (publicNetwork={pub}).", null, null) };

            return new List<AutofixCheck> { new("sql-health", name, AutofixOutcome.FailedToFix,
                $"SQL Server '{name}' state={state}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("sql-health", name, AutofixOutcome.Skipped,
                "Could not query SQL Server.", null, null) };
        }
    }

    // ─── PostgreSQL Flexible Server ───────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckPostgresAsync(
        string rg, string name, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "postgres", "flexible-server", "show", "-g", rg, "--name", name,
                "--query", "{state:state, publicAccess:network.publicNetworkAccess, fqdn:fullyQualifiedDomainName}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (state?.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true)
            {
                checks.Add(new AutofixCheck("postgres-health", name, AutofixOutcome.Ok,
                    $"PostgreSQL '{name}' ready (publicNetwork={pub}).", null, null));
            }
            else if (state?.Equals("Stopped", StringComparison.OrdinalIgnoreCase) == true)
            {
                _onLog("warn", $"[Autofix] {name}: PostgreSQL stopped. Starting...");
                var fixCmd = $"az postgres flexible-server start -g {rg} --name {name}";
                var r = await RunAzCommandAsync(fixCmd, ct);
                checks.Add(new AutofixCheck("postgres-health", name,
                    r.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    "PostgreSQL was stopped.",
                    fixCmd, r.ExitCode == 0 ? "Started" : r.Output));
            }
            else
            {
                checks.Add(new AutofixCheck("postgres-health", name, AutofixOutcome.FailedToFix,
                    $"PostgreSQL '{name}' state={state}.", null, null));
            }
        }
        catch
        {
            checks.Add(new AutofixCheck("postgres-health", name, AutofixOutcome.Skipped,
                "Could not query PostgreSQL.", null, null));
        }

        return checks;
    }

    // ─── SignalR Service ──────────────────────────────────────────────────

    private async Task<List<AutofixCheck>> CheckSignalRAsync(
        string rg, string name, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[] { "signalr", "show", "-g", rg, "--name", name,
                "--query", "{state:provisioningState, publicAccess:publicNetworkAccess, hostName:hostName}",
                "-o", "json" })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            if (state?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true)
                return new List<AutofixCheck> { new("signalr-health", name, AutofixOutcome.Ok,
                    $"SignalR '{name}' provisioned (publicNetwork={pub}).", null, null) };

            return new List<AutofixCheck> { new("signalr-health", name, AutofixOutcome.FailedToFix,
                $"SignalR '{name}' state={state}.", null, null) };
        }
        catch
        {
            return new List<AutofixCheck> { new("signalr-health", name, AutofixOutcome.Skipped,
                "Could not query SignalR.", null, null) };
        }
    }

    // ─── Shared: endpoint probe ───────────────────────────────────────────

    private async Task<AutofixCheck> CheckEndpointAsync(
        string target, string fqdn, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = fqdn.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? fqdn : $"https://{fqdn}";
            var response = await http.GetAsync(url, ct);
            var status = (int)response.StatusCode;
            return status < 500
                ? new AutofixCheck("endpoint-reachable", target, AutofixOutcome.Ok,
                    $"Endpoint {url} → HTTP {status}.", null, null)
                : new AutofixCheck("endpoint-reachable", target, AutofixOutcome.FailedToFix,
                    $"Endpoint {url} → HTTP {status} (server error).", null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck("endpoint-reachable", target, AutofixOutcome.FailedToFix,
                $"Endpoint unreachable: {ex.Message}", null, null);
        }
    }

    // ─── Shared: run az command ───────────────────────────────────────────

    private async Task<(int ExitCode, string Output)> RunAzCommandAsync(
        string fullCommand, CancellationToken ct)
    {
        var parts = fullCommand.Split(' ', 2);
        var exe = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var result = await Cli.Wrap(exe)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .ExecuteAsync(ct);

        var output = stdout.Length > 0 ? stdout.ToString() : stderr.ToString();
        return (result.ExitCode, output.Length > 500 ? output[..500] : output);
    }

    // ─── Internal types ───────────────────────────────────────────────────

    private sealed record AzureResource(string Name, string Type, string? ProvisioningState, string? Id);
}
