namespace AgentStationHub.Models;

public enum DeploymentTarget
{
    CopilotStudio,
    AzureAIFoundry,
    Both
}

public enum AgentCategory
{
    Medical,
    Legal,
    Financial,
    CustomerService,
    HumanResources,
    Education,
    Retail,
    Manufacturing,
    Government,
    Productivity
}

/// <summary>
/// Pre-classification of how the orchestrator should treat the repo.
/// Avoids wasting a clone+plan cycle on libraries / courses / SDKs whose
/// `TechClassifier` verdict is already known to be NotDeployable, and
/// lets the UI greyscale the Deploy button with an actionable tooltip.
/// </summary>
public enum Deployability
{
    /// <summary>azd up / docker compose / similar — can be auto-deployed.</summary>
    Deployable,
    /// <summary>SDK, library, framework, course, samples-monorepo without a
    /// canonical deploy target. UI hides Deploy and shows "Explore on GitHub".</summary>
    NotApplicable,
    /// <summary>Copilot Studio template — manual import via portal only.</summary>
    ManualOnly
}

public sealed record AgentSample(
    string Id,
    string Name,
    string Description,
    AgentCategory Category,
    DeploymentTarget Target,
    string GitHubUrl,
    string? DeployToAzureUrl,
    string? CopilotStudioImportUrl,
    IReadOnlyList<string> Tags,
    int Stars,
    string Author)
{
    /// <summary>
    /// Optional sub-folder (relative to repo root) where the deployable
    /// project actually lives. Used for monorepos like
    /// <c>microsoft/agent-framework</c> whose deployable end-to-end sample
    /// is at <c>python/samples/05-end-to-end</c>. When null the orchestrator
    /// works from the repo root (the existing behaviour).
    /// </summary>
    public string? SamplePath { get; init; }

    /// <summary>
    /// Pre-classified deployability hint. Defaults to <c>Deployable</c>
    /// to keep the prior behaviour for entries that don't set it.
    /// </summary>
    public Deployability Deployability { get; init; } = Deployability.Deployable;
}
