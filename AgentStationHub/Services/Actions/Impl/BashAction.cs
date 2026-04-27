using System.Text.Json;
using AgentStationHub.Services.Tools;

namespace AgentStationHub.Services.Actions.Impl;

/// <summary>
/// Explicit shell-script escape hatch. Used when none of the typed
/// actions fit (file edits with sed/awk, find -exec patterns, anything
/// the model genuinely needs full shell flexibility for). Equivalent
/// to the legacy <c>DeploymentStep.Command</c> path � but going
/// through the typed pipeline keeps the code paths uniform and lets
/// us swap a Bash step for a typed action later without touching the
/// orchestrator.
///
/// Also doubles as the fallback for unknown action types (forward
/// compatibility): if the LLM emits a future <c>"type":"FooBar"</c>
/// we don't yet implement, the registry maps it to a Bash action that
/// surfaces the unknown type in the live log so the user can flag it.
/// </summary>
public sealed class BashAction : IDeployAction
{
    public string Type => "Bash";

    public string Script { get; }
    public string? Note { get; }

    public BashAction(string script, string? note = null)
    {
        Script = script ?? "";
        Note = note;
    }

    public static BashAction FromJson(JsonElement el)
    {
        // Two shapes accepted:
        //   { "type":"Bash", "script": "..." }     ← preferred
        //   { "type":"Bash", "cmd":    "..." }     ← legacy alias
        var script = el.OptString("script") ?? el.OptString("cmd") ?? "";
        return new BashAction(script);
    }

    /// <summary>Surrogate for unknown <c>type</c> values � keeps the deploy alive.</summary>
    public static BashAction Unknown(string type, string fullJson) =>
        new("",
            $"Unknown typed action '{type}'. JSON: {fullJson}");

    public string Describe(DeployContext ctx) =>
        Note ?? (Script.Length <= 80 ? Script : Script[..80] + "…");

    public async Task<ActionResult> ExecuteAsync(
        DeployContext ctx, DockerShellTool docker, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Script) && !string.IsNullOrEmpty(Note))
            return new ActionResult(2, Note, ActionErrorCategory.Validation);

        var result = await docker.RunAsync(
            Script, containerCwd: ".",
            envVars: ctx.Env,
            timeout: timeout, ct: ct);

        var category = result.ExitCode == 0
            ? ActionErrorCategory.Ok
            : (result.TimedOutBySilence ? ActionErrorCategory.BuildHang : ActionErrorCategory.Generic);
        return new ActionResult(result.ExitCode, result.TailLog, category);
    }
}
