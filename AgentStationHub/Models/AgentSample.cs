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
    string Author);
