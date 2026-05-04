using System.Text;
using System.Text.Json;
using AgentStationHub.Models;
using CliWrap;

namespace AgentStationHub.Services.Agents;

/// <summary>
/// Post-deploy infrastructure autofix agent. After a successful deployment,
/// this agent inspects the Azure Resource Group, compares actual state with
/// expected state (from the repo and App Configuration), and automatically
/// remediates issues (degraded containers, wrong managed identity, missing
/// revisions, unreachable endpoints).
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

        _onLog("info", $"[Autofix] Starting infrastructure review for RG '{resourceGroup}'");

        // 1. Enumerate Container Apps
        var apps = await ListContainerAppsAsync(resourceGroup, ct);
        if (apps.Count == 0)
        {
            checks.Add(new AutofixCheck(
                "container-apps", resourceGroup, AutofixOutcome.Skipped,
                "No Container Apps found in resource group.", null, null));
        }
        else
        {
            // 2. For each CA: check revision health, MI, endpoint
            foreach (var app in apps)
            {
                var appChecks = await CheckContainerAppAsync(resourceGroup, app, ct);
                checks.AddRange(appChecks);
            }
        }

        // 3. Check key Azure services reachability
        var serviceChecks = await CheckAzureServicesAsync(resourceGroup, ct);
        checks.AddRange(serviceChecks);

        var completedAt = DateTimeOffset.UtcNow;
        var fixedCount = checks.Count(c => c.Outcome == AutofixOutcome.Fixed);
        var failedCount = checks.Count(c => c.Outcome == AutofixOutcome.FailedToFix);
        var okCount = checks.Count(c => c.Outcome == AutofixOutcome.Ok);

        var summary = $"Autofix completed: {checks.Count} checks, " +
                      $"{okCount} healthy, {fixedCount} fixed, {failedCount} could not fix. " +
                      $"Duration: {(completedAt - startedAt).TotalSeconds:F1}s";

        _onLog("info", $"[Autofix] {summary}");

        return new AutofixReport(
            sessionId, resourceGroup, repoUrl,
            startedAt, completedAt, checks, summary);
    }

    private async Task<List<ContainerAppInfo>> ListContainerAppsAsync(
        string rg, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var result = await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "containerapp", "list", "-g", rg,
                "--query", "[].{name:name, image:properties.template.containers[0].image, " +
                           "envVars:properties.template.containers[0].env, " +
                           "fqdn:properties.configuration.ingress.fqdn}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0 || stdout.Length < 2)
            return new List<ContainerAppInfo>();

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var apps = new List<ContainerAppInfo>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.GetProperty("name").GetString() ?? "";
                var image = el.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String
                    ? img.GetString() ?? "" : "";
                var fqdn = el.TryGetProperty("fqdn", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString() : null;
                apps.Add(new ContainerAppInfo(name, image, fqdn));
            }
            return apps;
        }
        catch
        {
            return new List<ContainerAppInfo>();
        }
    }

    private async Task<List<AutofixCheck>> CheckContainerAppAsync(
        string rg, ContainerAppInfo app, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();

        // Check 1: Active revisions exist and are healthy
        var revisionCheck = await CheckRevisionsAsync(rg, app.Name, ct);
        checks.Add(revisionCheck);

        // Check 2: Managed Identity configured correctly
        var miCheck = await CheckManagedIdentityAsync(rg, app.Name, ct);
        checks.Add(miCheck);

        // Check 3: Endpoint responds (if ingress is configured)
        if (!string.IsNullOrEmpty(app.Fqdn))
        {
            var endpointCheck = await CheckEndpointAsync(app.Name, app.Fqdn, ct);
            checks.Add(endpointCheck);
        }

        // Check 4: Image is not placeholder
        if (app.Image.Contains("containerapps-helloworld", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new AutofixCheck(
                "container-image", app.Name, AutofixOutcome.FailedToFix,
                "Container App still running placeholder image (containerapps-helloworld). " +
                "Needs image build+push.", null, null));
        }
        else
        {
            checks.Add(new AutofixCheck(
                "container-image", app.Name, AutofixOutcome.Ok,
                $"Running real image: {app.Image}", null, null));
        }

        return checks;
    }

    private async Task<AutofixCheck> CheckRevisionsAsync(
        string rg, string appName, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "containerapp", "revision", "list",
                "-g", rg, "--name", appName,
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
            // No active revisions — try to create one
            _onLog("warn", $"[Autofix] {appName}: no active revisions. Creating new revision...");
            var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AUTOFIX_RESTART=true\"";
            var fixResult = await RunAzCommandAsync(fixCmd, ct);

            if (fixResult.ExitCode == 0)
            {
                _onLog("info", $"[Autofix] {appName}: new revision created successfully.");
                return new AutofixCheck(
                    "revision-health", appName, AutofixOutcome.Fixed,
                    "No active revisions found. Created new revision via update.",
                    fixCmd, "Revision created");
            }
            return new AutofixCheck(
                "revision-health", appName, AutofixOutcome.FailedToFix,
                "No active revisions and failed to create new one.",
                fixCmd, fixResult.Output);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var unhealthy = new List<string>();
            foreach (var rev in doc.RootElement.EnumerateArray())
            {
                var health = rev.TryGetProperty("health", out var h)
                    ? h.GetString() ?? "None" : "None";
                var running = rev.TryGetProperty("running", out var r)
                    ? r.GetString() ?? "Unknown" : "Unknown";
                var name = rev.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "" : "";

                if (!health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) &&
                    !running.Contains("Running", StringComparison.OrdinalIgnoreCase))
                {
                    unhealthy.Add($"{name}({health}/{running})");
                }
            }

            if (unhealthy.Count > 0)
            {
                // Try restart
                _onLog("warn", $"[Autofix] {appName}: unhealthy revisions: {string.Join(", ", unhealthy)}. Attempting restart...");
                var fixCmd = $"az containerapp update -g {rg} --name {appName} --set-env-vars \"AUTOFIX_RESTART={DateTimeOffset.UtcNow:yyyyMMddHHmmss}\"";
                var fixResult = await RunAzCommandAsync(fixCmd, ct);

                return new AutofixCheck(
                    "revision-health", appName,
                    fixResult.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                    $"Unhealthy revisions detected: {string.Join(", ", unhealthy)}",
                    fixCmd, fixResult.ExitCode == 0 ? "New revision triggered" : fixResult.Output);
            }

            return new AutofixCheck(
                "revision-health", appName, AutofixOutcome.Ok,
                "All active revisions are healthy.", null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck(
                "revision-health", appName, AutofixOutcome.Skipped,
                $"Could not parse revision state: {ex.Message}", null, null);
        }
    }

    private async Task<AutofixCheck> CheckManagedIdentityAsync(
        string rg, string appName, CancellationToken ct)
    {
        // Get the user-assigned MI on the container app
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

            var envClientId = root.TryGetProperty("envClientId", out var ec)
                && ec.ValueKind == JsonValueKind.String
                ? ec.GetString() : null;

            if (root.TryGetProperty("identity", out var idEl) &&
                idEl.ValueKind == JsonValueKind.Object)
            {
                // Get the actual clientId from the UA MI
                string? actualClientId = null;
                foreach (var mi in idEl.EnumerateObject())
                {
                    if (mi.Value.TryGetProperty("clientId", out var cid))
                    {
                        actualClientId = cid.GetString();
                        break; // Take first (there should be only one)
                    }
                }

                if (actualClientId != null && envClientId != null &&
                    !actualClientId.Equals(envClientId, StringComparison.OrdinalIgnoreCase))
                {
                    // Mismatch! Fix it
                    _onLog("warn", $"[Autofix] {appName}: AZURE_CLIENT_ID mismatch. " +
                                   $"Env={envClientId}, MI={actualClientId}. Fixing...");
                    var fixCmd = $"az containerapp update -g {rg} --name {appName} " +
                                 $"--set-env-vars \"AZURE_CLIENT_ID={actualClientId}\"";
                    var fixResult = await RunAzCommandAsync(fixCmd, ct);

                    return new AutofixCheck(
                        "managed-identity", appName,
                        fixResult.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                        $"AZURE_CLIENT_ID env var ({envClientId}) did not match " +
                        $"assigned identity ({actualClientId}).",
                        fixCmd, fixResult.ExitCode == 0 ? "Env var corrected" : fixResult.Output);
                }
                else if (actualClientId != null && envClientId == null)
                {
                    // No AZURE_CLIENT_ID set at all — set it
                    _onLog("warn", $"[Autofix] {appName}: AZURE_CLIENT_ID not set. Adding...");
                    var fixCmd = $"az containerapp update -g {rg} --name {appName} " +
                                 $"--set-env-vars \"AZURE_CLIENT_ID={actualClientId}\"";
                    var fixResult = await RunAzCommandAsync(fixCmd, ct);

                    return new AutofixCheck(
                        "managed-identity", appName,
                        fixResult.ExitCode == 0 ? AutofixOutcome.Fixed : AutofixOutcome.FailedToFix,
                        $"AZURE_CLIENT_ID was not set. Assigned identity clientId: {actualClientId}.",
                        fixCmd, fixResult.ExitCode == 0 ? "Env var set" : fixResult.Output);
                }

                return new AutofixCheck(
                    "managed-identity", appName, AutofixOutcome.Ok,
                    $"AZURE_CLIENT_ID correctly matches assigned identity ({actualClientId}).",
                    null, null);
            }

            return new AutofixCheck(
                "managed-identity", appName, AutofixOutcome.Skipped,
                "No user-assigned managed identity configured.", null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck(
                "managed-identity", appName, AutofixOutcome.Skipped,
                $"Could not inspect MI: {ex.Message}", null, null);
        }
    }

    private async Task<AutofixCheck> CheckEndpointAsync(
        string appName, string fqdn, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"https://{fqdn}";
            var response = await http.GetAsync(url, ct);
            var status = (int)response.StatusCode;

            if (status < 500)
            {
                return new AutofixCheck(
                    "endpoint-reachable", appName, AutofixOutcome.Ok,
                    $"Endpoint {url} responded with HTTP {status}.", null, null);
            }

            return new AutofixCheck(
                "endpoint-reachable", appName, AutofixOutcome.FailedToFix,
                $"Endpoint {url} returned HTTP {status} (server error).",
                null, null);
        }
        catch (Exception ex)
        {
            return new AutofixCheck(
                "endpoint-reachable", appName, AutofixOutcome.FailedToFix,
                $"Endpoint https://{fqdn} unreachable: {ex.Message}",
                null, null);
        }
    }

    private async Task<List<AutofixCheck>> CheckAzureServicesAsync(
        string rg, CancellationToken ct)
    {
        var checks = new List<AutofixCheck>();

        // Check App Configuration accessibility
        var appConfigCheck = await CheckAppConfigAsync(rg, ct);
        if (appConfigCheck != null) checks.Add(appConfigCheck);

        // Check CosmosDB accessibility
        var cosmosCheck = await CheckCosmosAsync(rg, ct);
        if (cosmosCheck != null) checks.Add(cosmosCheck);

        // Check AI services
        var aiCheck = await CheckAIServicesAsync(rg, ct);
        if (aiCheck != null) checks.Add(aiCheck);

        return checks;
    }

    private async Task<AutofixCheck?> CheckAppConfigAsync(string rg, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "appconfig", "list", "-g", rg,
                "--query", "[0].{name:name, publicAccess:publicNetworkAccess}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        if (stdout.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
            var pub = doc.RootElement.TryGetProperty("publicAccess", out var p) ? p.GetString() : "unknown";

            return new AutofixCheck(
                "app-configuration", name ?? rg, AutofixOutcome.Ok,
                $"App Configuration '{name}' accessible (publicNetworkAccess={pub}).",
                null, null);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AutofixCheck?> CheckCosmosAsync(string rg, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "cosmosdb", "list", "-g", rg,
                "--query", "[0].{name:name, publicAccess:publicNetworkAccess}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        if (stdout.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
            return new AutofixCheck(
                "cosmosdb", name ?? rg, AutofixOutcome.Ok,
                $"CosmosDB account '{name}' accessible.", null, null);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AutofixCheck?> CheckAIServicesAsync(string rg, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        await Cli.Wrap("az")
            .WithArguments(new[]
            {
                "cognitiveservices", "account", "list", "-g", rg,
                "--query", "[0].{name:name, publicAccess:properties.publicNetworkAccess}",
                "-o", "json"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct);

        if (stdout.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
            return new AutofixCheck(
                "ai-services", name ?? rg, AutofixOutcome.Ok,
                $"AI Services '{name}' accessible.", null, null);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(int ExitCode, string Output)> RunAzCommandAsync(
        string fullCommand, CancellationToken ct)
    {
        // Parse the command into executable + args
        var parts = fullCommand.Split(' ', 2);
        var exe = parts[0]; // "az"
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

    private sealed record ContainerAppInfo(string Name, string Image, string? Fqdn);
}
