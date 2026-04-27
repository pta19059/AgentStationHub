using System.Text.Json;
using AgentStationHub.Services.Tools;

namespace AgentStationHub.Services.Actions.Impl;

/// <summary>
/// Build a Docker image on Azure Container Registry remote build.
/// Replaces the brittle one-liner the LLM kept reinventing:
/// <code>
///   bash -lc "cd /workspace/packages/ui-angular &amp;&amp; \
///     az acr build --registry $(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | cut -d= -f2 | tr -d '\"') \
///                   --image ui-angular:azd-$(date +%s) --file Dockerfile . --no-logs"
/// </code>
///
/// The typed version takes only the things that vary: which service
/// folder, which Dockerfile, the image name. The registry comes from
/// <see cref="DeployContext.AcrName"/> (populated deterministically
/// after <c>azd provision</c>); the tag is generated server-side as
/// <c>azd-&lt;unixSec&gt;</c> for unique-per-build determinism without
/// asking the model to inject a timestamp.
/// </summary>
public sealed class AcrBuildAction : IDeployAction
{
    public string Type => "AcrBuild";

    public string Service { get; }
    public string ContextDir { get; }
    public string Dockerfile { get; }
    public string ImageName { get; }

    public AcrBuildAction(string service, string contextDir, string dockerfile, string imageName)
    {
        Service = service;
        ContextDir = string.IsNullOrWhiteSpace(contextDir) ? "." : contextDir;
        Dockerfile = string.IsNullOrWhiteSpace(dockerfile) ? "Dockerfile" : dockerfile;
        ImageName = imageName;
    }

    public static AcrBuildAction FromJson(JsonElement el) =>
        new(
            service: el.ReqString("service"),
            contextDir: el.OptString("contextDir") ?? ".",
            dockerfile: el.OptString("dockerfile") ?? "Dockerfile",
            imageName: el.OptString("imageName") ?? el.ReqString("service"));

    public string Describe(DeployContext ctx)
        => $"Build {Service} on ACR {ctx.AcrName ?? "<unresolved>"} (context: {ContextDir}, dockerfile: {Dockerfile})";

    public async Task<ActionResult> ExecuteAsync(
        DeployContext ctx, DockerShellTool docker, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.AcrName))
        {
            return new ActionResult(
                ExitCode: 2,
                TailLog: "AcrBuild requires DeployContext.AcrName but none is set. " +
                         "Run azd provision first (it populates AZURE_CONTAINER_REGISTRY_ENDPOINT).",
                Category: ActionErrorCategory.MissingArgument);
        }

        var tag = $"azd-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var fullImage = $"{ImageName}:{tag}";

        // We still go through DockerShellTool because the sandbox
        // session container, az CLI prewarm, ANSI/secret redaction,
        // silence watchdog and step-log streaming are all baked in
        // there. But the command we hand it is composed in C# from
        // typed parameters � no $(...), no shell expansion, no quote
        // nesting. The resulting argv is the canonical az invocation
        // documented at https://aka.ms/cli/acr/build.
        var cmd =
            $"az acr build " +
            $"--registry {ctx.AcrName} " +
            $"--image {fullImage} " +
            $"--file {Shell.QuoteIfNeeded(Dockerfile)} " +
            $"--no-logs " +
            ".";

        var result = await docker.RunAsync(
            cmd, ContextDir,
            envVars: ctx.Env,
            timeout: timeout,
            ct: ct);

        if (result.ExitCode == 0)
        {
            var fullRef = ctx.AcrEndpoint is null
                ? fullImage
                : $"{ctx.AcrEndpoint}/{fullImage}";
            ctx.WithService(Service, info => info.LastBuiltImageRef = fullRef);
            return new ActionResult(0, result.TailLog);
        }

        var category = ClassifyError(result.TailLog, result.TimedOutBySilence);
        return new ActionResult(result.ExitCode, result.TailLog, category);
    }

    private static ActionErrorCategory ClassifyError(string tail, bool hung)
    {
        if (hung) return ActionErrorCategory.BuildHang;
        if (tail.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("denied:", StringComparison.OrdinalIgnoreCase))
            return ActionErrorCategory.AuthDenied;
        if (tail.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return ActionErrorCategory.QuotaExceeded;
        if (tail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase))
            return ActionErrorCategory.NotFound;
        return ActionErrorCategory.Generic;
    }
}

internal static class Shell
{
    /// <summary>Single-quote a value if it contains shell-unsafe chars.</summary>
    public static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        foreach (var c in value)
        {
            var safe = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':';
            if (!safe) return "'" + value.Replace("'", "'\\''") + "'";
        }
        return value;
    }
}
