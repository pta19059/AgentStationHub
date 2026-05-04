namespace AgentStationHub.Models;

public enum DeploymentStatus
{
    Pending,
    Cloning,
    Inspecting,
    Planning,
    AwaitingApproval,
    Rejected,
    Executing,
    Verifying,
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>
    /// Terminal state: the repository is not a deployment target (course,
    /// tutorial, pure library, docs site). Different from Failed � the
    /// pipeline did its job, the repo simply doesn't have anything to run.
    /// </summary>
    NotDeployable,
    /// <summary>
    /// Terminal state: the Doctor (in-sandbox or hosted Foundry) emitted an
    /// [Escalate] verdict. The repository IS a deployment target, but the
    /// failure is rooted in the repo source itself (missing azure.yaml,
    /// broken Bicep, corrupt lockfile, ...) and cannot be patched from
    /// inside the sandbox. Different from Failed: the pipeline did its job
    /// and correctly identified that the next move is on the user (open a
    /// PR on the source repo, or pick a different sample). The UI surfaces
    /// this as an INFO alert, not a red error box.
    /// </summary>
    BlockedNeedsHumanOrSourceFix
}

public sealed record DeploymentStep(
    int Id,
    string Description,
    string Command,
    string WorkingDirectory = ".",
    TimeSpan? Timeout = null)
{
    /// <summary>
    /// Optional typed-action JSON. When non-null the orchestrator
    /// routes through <c>ActionRegistry.TryParse</c> + the typed
    /// <c>IDeployAction.ExecuteAsync</c> path instead of executing
    /// <see cref="Command"/> as a shell string. Backward compatible:
    /// existing plans without this field execute exactly as before.
    /// See <c>Services/Actions/IDeployAction.cs</c> for the rationale
    /// (eliminates the "LLM writes brittle bash pipelines" failure mode).
    /// </summary>
    public string? ActionJson { get; init; }
}

public sealed record DeploymentPlan(
    string Repo,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<DeploymentStep> Steps,
    IReadOnlyList<string> VerifyHints)
{
    /// <summary>
    /// Classification signal from the Scout + TechClassifier: 'app',
    /// 'course', 'library', 'samples', 'docs', 'cli', 'monorepo', 'unknown'.
    /// Null means the classifier did not produce a kind (legacy path).
    /// </summary>
    public string? RepoKind { get; init; }

    /// <summary>
    /// When false the orchestrator skips execution entirely and surfaces
    /// <see cref="NotDeployableReason"/> to the user. Default is true so
    /// existing behaviour is preserved when the classifier is silent.
    /// </summary>
    public bool IsDeployable { get; init; } = true;

    /// <summary>
    /// User-facing explanation of why the repo is not a deploy target
    /// (e.g. "This repository is a 10-lesson course with 47 Jupyter
    /// notebooks. It has no deployable infrastructure.").
    /// </summary>
    public string? NotDeployableReason { get; init; }
}

/// <summary>
/// Fix proposed by the DeploymentDoctor agent after a step failure.
/// Kind: "replace_step" | "insert_before" | "give_up".
/// </summary>
public sealed record Remediation(
    string Kind,
    int StepId,
    IReadOnlyList<DeploymentStep> NewSteps,
    string? Reasoning);

public sealed record LogEntry(
    DateTime AtUtc,
    string Level,          // info | err | status
    string Message,
    int? StepId = null);

public sealed class DeploymentSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public required string RepoUrl { get; init; }
    public required string WorkDir { get; init; }
    /// <summary>
    /// Optional sub-folder relative to <see cref="WorkDir"/> where the
    /// actual deployable project lives. Used for monorepo catalog
    /// entries (e.g. <c>microsoft/agent-framework</c> with
    /// <c>SamplePath = "python/samples/05-end-to-end"</c>). When null
    /// the orchestrator works from <see cref="WorkDir"/> directly.
    /// </summary>
    public string? SamplePath { get; init; }
    /// <summary>
    /// Azure region chosen by the user for this deploy. Passed through to
    /// the agent team so the Strategist emits the correct 'azd env set
    /// AZURE_LOCATION &lt;x&gt;' (and sibling location env vars) in the plan.
    /// </summary>
    public required string AzureLocation { get; init; }

    /// <summary>
    /// Optional Azure AD tenant id chosen by the user up-front in the UI.
    /// When set, the sandbox 'az login --use-device-code' is pinned to
    /// this tenant from the very first deploy, removing the loop where
    /// the cached login lands on a default tenant that has no
    /// subscription / no permission to deploy. If the existing cached
    /// profile inside the sandbox volume is bound to a DIFFERENT tenant,
    /// the auth helper invalidates it and triggers a fresh device-code
    /// login on the right tenant. Null = legacy behaviour (no pinning).
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Optional Azure subscription id chosen by the user up-front. After
    /// login the sandbox runs <c>az account set --subscription &lt;id&gt;</c>
    /// so every subsequent step (azd up, az group create, ...) targets
    /// this subscription, even when the tenant has many. Null means
    /// "let azd / az pick the default subscription of the logged-in
    /// identity".
    /// </summary>
    public string? SubscriptionId { get; init; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Pending;
    public DeploymentPlan? Plan { get; set; }
    public List<LogEntry> Logs { get; } = new();
    public string? FinalEndpoint { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AutofixReportPath { get; set; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// Gates the orchestrator's transition from AwaitingApproval to
    /// Executing. 'RunContinuationsAsynchronously' is CRITICAL: without it
    /// TrySetResult() runs the deploy's continuation (SetStatus + long
    /// Docker operations) inline on the SignalR dispatch thread that
    /// handled the 'Approve &amp; run' click, blocking the Blazor circuit
    /// until the whole Executing phase finishes � the UI looks frozen.
    /// With the flag set, the continuation posts onto the thread pool and
    /// the click returns immediately.
    /// </summary>
    public TaskCompletionSource ApprovalTcs { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationTokenSource Cts { get; } = new();
}
