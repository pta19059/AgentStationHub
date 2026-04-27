using System.Text.Json;
using AgentStationHub.Services.Actions.Impl;

namespace AgentStationHub.Services.Actions;

/// <summary>
/// Polymorphic JSON deserialization for <see cref="IDeployAction"/>.
/// Discriminator: <c>"type"</c>. Unknown types fall back to
/// <see cref="BashAction"/> so a future Strategist that emits an
/// unrecognized action keyword still produces an executable step
/// (will be flagged by the Doctor on first failure rather than
/// hard-crashing the orchestrator).
///
/// The catalogue is intentionally tiny on purpose: each new action
/// must justify its existence. Bloating the catalogue makes the
/// LLM's job harder (more options to choose from) without improving
/// reliability. The current set covers the operations we measurably
/// looped on:
/// <list type="bullet">
///   <item><c>AcrBuild</c> � replaces every <c>$(azd env get-values |
///         grep AZURE_CONTAINER_REGISTRY_NAME | ...)</c> lookup.</item>
///   <item><c>ContainerAppUpdate</c> � replaces the
///         <c>az containerapp update -n X -g $RG --image $LOGIN/$X:$TAG</c>
///         pipeline that combined three brittle substitutions.</item>
///   <item><c>AzdEnvSet</c> � replaces
///         <c>azd env set KEY $(az account show --query id -o tsv)</c>
///         which was loop-prone whenever the value contained spaces.</item>
///   <item><c>Bash</c> � explicit escape hatch when the LLM genuinely
///         needs shell flexibility (sed, find, etc.). Routed through
///         the same <c>DockerShellTool</c> path used by legacy steps,
///         so the env-loader pre/post hooks still apply.</item>
/// </list>
/// </summary>
public static class ActionRegistry
{
    /// <summary>
    /// Parse a JSON object into an <see cref="IDeployAction"/>.
    /// Returns null on missing/unrecognized JSON; the caller treats
    /// that as "this step has no typed action, fall back to legacy
    /// Command path".
    /// </summary>
    public static IDeployAction? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String) return null;

            var type = typeEl.GetString() ?? "";
            return type switch
            {
                "AcrBuild" => AcrBuildAction.FromJson(root),
                "ContainerAppUpdate" => ContainerAppUpdateAction.FromJson(root),
                "AzdEnvSet" => AzdEnvSetAction.FromJson(root),
                "Bash" => BashAction.FromJson(root),
                _ => BashAction.Unknown(type, json),
            };
        }
    }

    /// <summary>
    /// Schema text that gets injected into the Doctor's system prompt.
    /// Keep this short and example-driven � LLMs choose better with
    /// a working example than with a JSON schema.
    /// </summary>
    public static string LlmCatalogueDescription => """
        TYPED ACTIONS (preferred over inline bash for the operations below)
        ──────────────────────────────────────────────────────────────────
        Instead of a "cmd" field, a step may carry an "action" object that
        the orchestrator executes via robust C# argv invocations � no
        shell parsing, no quote-nesting, no env-export fragility, no
        missing-binary surprises (awk/sed/cut). Use these whenever the
        operation matches; fall back to "cmd" for everything else.

        1) Build a Docker image on ACR remote build (avoids local docker-build hangs):
           {
             "id": 12,
             "description": "Build ui-angular on ACR",
             "action": {
               "type": "AcrBuild",
               "service": "ui-angular",
               "contextDir": "packages/ui-angular",
               "dockerfile": "Dockerfile.production",
               "imageName": "ui-angular"
             },
             "cwd": "."
           }
           The orchestrator resolves the registry name from DeployContext
           (already populated by AzdEnvLoader). You do not need to pass
           `--registry`. The image is tagged with a unix timestamp.

        2) Update an existing Container App to a freshly built image:
           {
             "id": 13,
             "description": "Roll ui-angular Container App to the new image",
             "action": {
               "type": "ContainerAppUpdate",
               "service": "ui-angular",
               "imageRef": "$LASTBUILT"
             },
             "cwd": "."
           }
           "$LASTBUILT" expands to the most recent AcrBuild output for
           that service. ResourceGroup is taken from DeployContext.

        3) Set a value in the azd environment (no shell substitution):
           {
             "id": 4,
             "description": "Pin subscription",
             "action": {
               "type": "AzdEnvSet",
               "key": "AZURE_SUBSCRIPTION_ID",
               "valueFrom": "AzAccountSubscriptionId"
             }
           }
           "valueFrom" can be: "AzAccountSubscriptionId", "AzAccountTenantId",
           or omitted in favour of "value" for a literal string.

        4) Escape hatch when none of the above fits:
           {
             "id": 6,
             "description": "Tweak bicep capacity",
             "action": {
               "type": "Bash",
               "script": "sed -i 's/capacity: 50/capacity: 1/g' infra/main.bicep"
             },
             "cwd": "."
           }

        Prefer typed actions when available. Bash only for one-shot file
        edits, not for orchestration.
        """;
}

/// <summary>
/// Convenience helpers used by every Impl/*.cs to extract typed
/// fields from the parsed JSON without ceremony.
/// </summary>
internal static class JsonReadHelpers
{
    public static string? OptString(this JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static string ReqString(this JsonElement el, string name)
        => el.OptString(name)
            ?? throw new ArgumentException($"missing required string field '{name}'");

    public static int? OptInt(this JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;
}
