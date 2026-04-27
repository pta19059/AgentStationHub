using System.Text.Json;
using AgentStationHub.Services.Tools;

namespace AgentStationHub.Services.Actions.Impl;

/// <summary>
/// Set a value in the current azd environment without going through
/// inline shell substitution. Replaces patterns like:
/// <code>
///   bash -lc "azd env set AZURE_SUBSCRIPTION_ID $(az account show --query id -o tsv)"
/// </code>
/// which were prone to:
/// <list type="bullet">
///   <item>Empty values when the inner command failed (silent
///         "azd env set KEY", which azd tolerates and produces an
///         empty value for).</item>
///   <item>Quote collisions on values containing spaces or quotes.</item>
///   <item>cwd-fragility (azd env set must run at the project root).</item>
/// </list>
///
/// Typed inputs:
/// <list type="bullet">
///   <item><c>key</c>: env variable name (validated as a typical UPPER_SNAKE).</item>
///   <item><c>value</c>: literal value. Optional if <c>valueFrom</c> is set.</item>
///   <item><c>valueFrom</c>: a well-known dynamic source. Currently:
///         <list type="bullet">
///           <item><c>AzAccountSubscriptionId</c> -> <c>az account show --query id -o tsv</c></item>
///           <item><c>AzAccountTenantId</c>      -> <c>az account show --query tenantId -o tsv</c></item>
///         </list>
///         Resolved by the action in C# and passed as a literal to azd.</item>
/// </list>
/// </summary>
public sealed class AzdEnvSetAction : IDeployAction
{
    public string Type => "AzdEnvSet";

    public string Key { get; }
    public string? Value { get; }
    public string? ValueFrom { get; }

    public AzdEnvSetAction(string key, string? value, string? valueFrom)
    {
        Key = key;
        Value = value;
        ValueFrom = valueFrom;
    }

    public static AzdEnvSetAction FromJson(JsonElement el) =>
        new(
            key: el.ReqString("key"),
            value: el.OptString("value"),
            valueFrom: el.OptString("valueFrom"));

    public string Describe(DeployContext ctx)
        => $"azd env set {Key}={Value ?? $"<from:{ValueFrom}>"}";

    public async Task<ActionResult> ExecuteAsync(
        DeployContext ctx, DockerShellTool docker, TimeSpan timeout, CancellationToken ct)
    {
        var resolved = Value;

        if (string.IsNullOrEmpty(resolved) && !string.IsNullOrEmpty(ValueFrom))
        {
            var dynCmd = ValueFrom switch
            {
                "AzAccountSubscriptionId" => "az account show --query id -o tsv",
                "AzAccountTenantId" => "az account show --query tenantId -o tsv",
                _ => null
            };
            if (dynCmd is null)
                return new ActionResult(2,
                    $"AzdEnvSet: unsupported valueFrom '{ValueFrom}'. " +
                    "Allowed: AzAccountSubscriptionId, AzAccountTenantId.",
                    ActionErrorCategory.Validation);

            var probe = await docker.RunAsync(
                dynCmd, containerCwd: ".",
                envVars: ctx.Env,
                timeout: TimeSpan.FromSeconds(45), ct: ct,
                tailSize: 20);
            if (probe.ExitCode != 0)
                return new ActionResult(probe.ExitCode,
                    $"AzdEnvSet: failed to resolve {ValueFrom} via `{dynCmd}`. {probe.TailLog}",
                    ActionErrorCategory.Generic);
            resolved = ExtractTrimmedTail(probe.TailLog);
        }

        if (string.IsNullOrWhiteSpace(resolved))
            return new ActionResult(2,
                "AzdEnvSet: resolved value is empty. Refusing to set an empty azd env value.",
                ActionErrorCategory.Validation);

        // azd env set parses argv directly; no shell quoting needed.
        // Spaces in the value are still tricky for the underlying
        // exec wrapper, so we single-quote conservatively.
        var cmd = $"azd env set {Key} {Shell.QuoteIfNeeded(resolved)}";
        var result = await docker.RunAsync(
            cmd, containerCwd: ".",
            envVars: ctx.Env,
            timeout: timeout, ct: ct);

        if (result.ExitCode == 0)
        {
            ctx.Env[Key] = resolved;
            return new ActionResult(0, result.TailLog);
        }
        return new ActionResult(result.ExitCode, result.TailLog, ActionErrorCategory.Generic);
    }

    private static string ExtractTrimmedTail(string tail)
    {
        // The tail contains the prewarm prelude lines too. Take the
        // LAST non-empty line as the canonical value (az --query -o tsv
        // emits exactly one line for scalar queries).
        if (string.IsNullOrEmpty(tail)) return "";
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            return t;
        }
        return "";
    }
}
