using AgentStationHub.Services.Tools;

namespace AgentStationHub.Services.Actions;

/// <summary>
/// A typed deploy action. The orchestrator executes these instead of
/// LLM-authored bash strings. Each action is responsible for:
/// <list type="bullet">
///   <item>Validating its own parameters (constructor or
///         <see cref="ValidationError"/>).</item>
///   <item>Resolving any <c>$AZURE_X</c>/<c>$SERVICE_X</c> placeholders
///         from <see cref="DeployContext"/> BEFORE executing � the
///         action layer never relies on shell expansion.</item>
///   <item>Executing the work via direct argv invocations
///         (<see cref="DockerShellTool"/> or
///         <see cref="SandboxSession"/>) � NEVER wrapped in
///         <c>bash -lc</c> unless the user authored it that way as a
///         literal escape hatch (<see cref="Impl.BashAction"/>).</item>
/// </list>
///
/// ## Why this exists
/// We were burning Doctor remediation budget on shape-of-pipeline
/// variations of fragile inline shell substitutions like
/// <c>REG=$(azd env get-values | grep ... | cut -d= -f2 | tr -d '"')</c>.
/// Every layer (the planner, the doctor, the security reviewer)
/// re-emitted slightly different bash that broke for slightly
/// different reasons (cwd-fragility, quote-nesting under
/// <c>bash -lc "..."</c>, login-shell env shadowing, missing
/// <c>awk</c>/<c>sed</c>/<c>tr</c> binaries, value-quoting collisions
/// with the outer step's bash wrapper). The deterministic substitutor
/// + alias loader closed many classes of these bugs but not the
/// generative model itself: it KEPT inventing new pipelines.
///
/// Typed actions remove the generative degree of freedom entirely.
/// The model still chooses WHICH action to run and WITH WHICH typed
/// parameters; the C# layer takes those parameters and builds the
/// correct argv. No bash, no quoting, no env-export hazards.
///
/// ## Coexistence with legacy steps
/// We do not migrate every step at once. <c>DeploymentStep</c> gains
/// an optional <c>ActionJson</c> field. When present, the orchestrator
/// routes through <see cref="ActionExecutor"/>. When null, the
/// existing <c>Command: string</c> path runs unchanged. The Strategist
/// continues to emit bash for trivial steps (sed, file-tweaks, oneshot
/// commands); the Doctor is taught to prefer typed actions for the
/// 6-7 deployment operations that historically caused loops.
/// </summary>
public interface IDeployAction
{
    /// <summary>
    /// Stable identifier exposed to the LLM (e.g. "AcrBuild",
    /// "ContainerAppUpdate"). MUST match the JSON discriminator
    /// in <see cref="ActionRegistry"/>.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// One-line, user-facing description used for live-log
    /// announcements ("� Step 7: Build ui-angular image on ACR
    /// crfoo123 (linux/amd64)").
    /// </summary>
    string Describe(DeployContext ctx);

    /// <summary>
    /// Execute the action. Implementations MUST be idempotent
    /// where possible (e.g. <c>az acr build</c> with the same
    /// image+tag is fine to re-run; <c>azd env set</c> too;
    /// <c>az containerapp update</c> too). Any meaningful outputs
    /// the action discovers (login server, image digest, revision
    /// name, endpoint URL) MUST be written into <paramref name="ctx"/>
    /// so subsequent actions and the Doctor can use them.
    /// </summary>
    Task<ActionResult> ExecuteAsync(
        DeployContext ctx,
        DockerShellTool docker,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Outcome of an <see cref="IDeployAction"/> run. <see cref="ExitCode"/>
/// follows the usual UNIX convention (0 = success). <see cref="Category"/>
/// classifies non-zero exits so the Doctor can pattern-match without
/// re-parsing stdout/stderr; this is what enables the Doctor to switch
/// strategy deterministically (e.g. on <c>BuildHang</c> propose
/// <see cref="Impl.AcrBuildAction"/> instead of letting it loop on
/// local-build remediations).
/// </summary>
public sealed record ActionResult(
    int ExitCode,
    string TailLog,
    ActionErrorCategory Category = ActionErrorCategory.Ok,
    string? Detail = null);

public enum ActionErrorCategory
{
    Ok,
    /// <summary>Generic failure; orchestrator falls back to default Doctor flow.</summary>
    Generic,
    /// <summary>Step was killed by the silence watchdog (Hang).</summary>
    BuildHang,
    /// <summary>az/azd reported "argument expected" / missing required flag value.</summary>
    MissingArgument,
    /// <summary>Auth or RBAC denied the operation.</summary>
    AuthDenied,
    /// <summary>Quota or capacity exceeded for a region/resource.</summary>
    QuotaExceeded,
    /// <summary>Resource not found (RG, container app, registry).</summary>
    NotFound,
    /// <summary>Validation failed before invocation (typed-action precondition).</summary>
    Validation,
}
