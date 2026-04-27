using System.Text.Json;
using AgentStationHub.Services.Tools;

namespace AgentStationHub.Services.Actions.Impl;

/// <summary>
/// Update an existing Azure Container App to a new image. Replaces
/// the LLM-prone:
/// <code>
///   bash -lc "RG=$(azd env get-values | grep AZURE_RESOURCE_GROUP | ...); \
///             ACR=$(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | ...); \
///             LOGIN=$(az acr show -n $ACR --query loginServer -o tsv); \
///             TAG=$(az acr repository show-tags -n $ACR --repository ui-angular --orderby time_desc --top 1 -o tsv); \
///             az containerapp update -n ui-angular -g $RG --image $LOGIN/ui-angular:$TAG"
/// </code>
/// which combined four brittle substitutions into a single line.
///
/// Typed inputs:
/// <list type="bullet">
///   <item><c>service</c>: name of the Container App in the RG. Used
///         both as <c>-n</c> and as a key into <see cref="DeployContext.Services"/>.</item>
///   <item><c>imageRef</c>: full image reference. The literal string
///         <c>"$LASTBUILT"</c> resolves to the most recent
///         <see cref="ServiceInfo.LastBuiltImageRef"/> for this service
///         (set by <see cref="AcrBuildAction"/>). Otherwise the value
///         is used verbatim.</item>
/// </list>
/// Resource group is resolved from <see cref="DeployContext.ResourceGroup"/>.
/// </summary>
public sealed class ContainerAppUpdateAction : IDeployAction
{
    public string Type => "ContainerAppUpdate";

    public string Service { get; }
    public string ImageRef { get; }

    public ContainerAppUpdateAction(string service, string imageRef)
    {
        Service = service;
        ImageRef = imageRef;
    }

    public static ContainerAppUpdateAction FromJson(JsonElement el) =>
        new(
            service: el.ReqString("service"),
            imageRef: el.OptString("imageRef") ?? "$LASTBUILT");

    public string Describe(DeployContext ctx)
        => $"Update Container App {Service} in {ctx.ResourceGroup ?? "<unresolved RG>"} -> {ResolveImage(ctx) ?? "<unresolved image>"}";

    public async Task<ActionResult> ExecuteAsync(
        DeployContext ctx, DockerShellTool docker, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.ResourceGroup))
            return new ActionResult(2,
                "ContainerAppUpdate requires DeployContext.ResourceGroup but none is set. " +
                "Run azd provision first (it populates the resource group).",
                ActionErrorCategory.MissingArgument);

        var image = ResolveImage(ctx);
        if (string.IsNullOrWhiteSpace(image))
            return new ActionResult(2,
                $"ContainerAppUpdate could not resolve image for service '{Service}'. " +
                "Hint: run AcrBuild first or pass a literal imageRef.",
                ActionErrorCategory.MissingArgument);

        var cmd =
            $"az containerapp update " +
            $"--name {Shell.QuoteIfNeeded(Service)} " +
            $"--resource-group {Shell.QuoteIfNeeded(ctx.ResourceGroup)} " +
            $"--image {Shell.QuoteIfNeeded(image)} " +
            "--only-show-errors";

        var result = await docker.RunAsync(
            cmd, containerCwd: ".",
            envVars: ctx.Env,
            timeout: timeout, ct: ct);

        if (result.ExitCode == 0)
        {
            ctx.WithService(Service, info => info.ResourceExists = true);
            return new ActionResult(0, result.TailLog);
        }
        return new ActionResult(result.ExitCode, result.TailLog,
            Classify(result.TailLog, result.TimedOutBySilence));
    }

    private string? ResolveImage(DeployContext ctx)
    {
        if (!string.Equals(ImageRef, "$LASTBUILT", StringComparison.Ordinal))
            return ImageRef;
        return ctx.Services.TryGetValue(Service.ToLowerInvariant(), out var info)
            ? info.LastBuiltImageRef
            : null;
    }

    private static ActionErrorCategory Classify(string tail, bool hung)
    {
        if (hung) return ActionErrorCategory.BuildHang;
        if (tail.Contains("not exist", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase))
            return ActionErrorCategory.NotFound;
        if (tail.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
            return ActionErrorCategory.AuthDenied;
        return ActionErrorCategory.Generic;
    }
}
