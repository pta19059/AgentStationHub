namespace AgentStationHub.Services;

public sealed class DeploymentOptions
{
    public bool AutoApprove { get; set; } = false;

    public string SandboxImage { get; set; } = "mcr.microsoft.com/azure-dev-cli-apps:latest";
    public string? WorkRootDir { get; set; }

    /// <summary>
    /// Default timeout for a generic step (azd env set, az account show,
    /// short shell commands). Defaults to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> � "let the
    /// step run to completion, the user cancels via the UI if really
    /// needed". Previously capped at 10 minutes which turned out to be
    /// too aggressive: 'az provider register' and similar ops legitimately
    /// exceed that on first-time-in-region registrations, while still
    /// not qualifying as long-running by our heuristic. Set a concrete
    /// TimeSpan here if you want the old behaviour back (useful for CI
    /// runs where a hung sandbox must be killed).
    /// </summary>
    public TimeSpan DefaultStepTimeout { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Timeout applied to commands detected as "long-running":
    ///   � 'azd up' / 'azd provision' / 'azd deploy' (subscription-level
    ///     Bicep deployments � 15-45 min typical).
    ///   � 'az group delete' without '--no-wait' (dependency-chain teardown
    ///     of OpenAI + Search + Container Apps + VNet can hit 30-40 min).
    ///   � 'az cognitiveservices account purge' / 'az keyvault purge' �
    ///     soft-delete retention can push the command past 15 min.
    /// Also defaults to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// so a genuinely slow subscription (multi-region Bicep, quota probes,
    /// dependency chains) runs to natural completion. Cancel via the UI
    /// to abort.
    /// </summary>
    public TimeSpan LongRunningStepTimeout { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Maximum wall-clock interval a step can go WITHOUT producing any
    /// stdout/stderr output before it's considered hung and aborted.
    /// Applies independently of the step timeout: even with
    /// <see cref="LongRunningStepTimeout"/> set to Infinite, a silent step
    /// after this window is treated as a hang and surfaces the 'StepSilent'
    /// error signature so the Doctor can propose a switch strategy.
    ///
    /// Defaults to 15 minutes: healthy azd / docker commands produce
    /// progress at least once per minute (layer pulls, [progress N:NN]
    /// azd polling lines, Bicep deployment ticks). A 15-minute gap is
    /// almost always a real hang (classic: 'docker buildx build' stuck
    /// inside an Angular 'npm install' that can't reach a private
    /// registry, azd deploy waiting on a revision that will never become
    /// healthy). Raise this if your subscription runs in a region with
    /// very long quiet phases between progress lines.
    /// </summary>
    public TimeSpan StepSilenceBudget { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Belt-and-braces cap on how many times the DeploymentDoctor agent
    /// is allowed to propose a remediation within a single deploy
    /// session. The Doctor is INSTRUCTED to emit "give_up" when its
    /// proposals are clearly going in circles (duplicate detection,
    /// signature oscillation, long PREVIOUS ATTEMPTS list), but a
    /// hallucinating model can still loop on micro-variations the
    /// near-duplicate guard doesn't catch. When this cap is reached,
    /// the orchestrator aborts with a clean "Doctor budget exhausted"
    /// error instead of the confusing "session cancelled" state the
    /// user would eventually get from MaxSessionDuration. Set to 0 to
    /// disable the budget entirely and rely only on the Doctor's own
    /// give_up logic.
    /// </summary>
    public int MaxDoctorInvocationsPerSession { get; set; } = 0;

    /// <summary>
    /// Default Azure region used by the Strategist when the user does not
    /// override it for a given deploy. The UI picker falls back to this.
    /// </summary>
    public string DefaultAzureLocation { get; set; } = "eastus";

    /// <summary>
    /// Regions offered in the UI location picker. The list is advisory; the
    /// orchestrator will accept any user-entered string because Azure region
    /// availability depends on the subscription's quota and feature flags.
    /// </summary>
    public List<string> AvailableAzureLocations { get; set; } = new()
    {
        "eastus", "eastus2", "westus2", "westus3", "centralus",
        "northeurope", "westeurope", "swedencentral", "uksouth",
        "australiaeast", "japaneast", "southeastasia"
    };
}
