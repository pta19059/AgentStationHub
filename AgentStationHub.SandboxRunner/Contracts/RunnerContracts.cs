using System.Text.Json.Serialization;

namespace AgentStationHub.SandboxRunner.Contracts;

// Protocol DTOs for the stdin/stdout JSON channel between the host app and
// the sandbox runner. Kept deliberately small and stable so the two sides
// can evolve independently as long as the shape is preserved.

public sealed record RunnerRequest(
    [property: JsonPropertyName("command")]     string Command,
    [property: JsonPropertyName("repoUrl")]     string? RepoUrl,
    [property: JsonPropertyName("workspace")]   string Workspace,
    [property: JsonPropertyName("logTail")]     string? LogTail,
    [property: JsonPropertyName("verifyHints")] IReadOnlyList<string>? VerifyHints,
    [property: JsonPropertyName("azureLocation")] string? AzureLocation,
    [property: JsonPropertyName("plan")]         DeploymentPlanDto? Plan,
    [property: JsonPropertyName("failedStepId")] int? FailedStepId,
    [property: JsonPropertyName("errorTail")]    string? ErrorTail,
    [property: JsonPropertyName("previousAttempts")] IReadOnlyList<string>? PreviousAttempts,
    // Insights carried over from previous deploys of the same repo
    // (successful AZURE_LOCATION, earlier Doctor give-up reasons, etc).
    // Optional � older hosts omit the field and the runner falls back to
    // the zero-memory flow.
    [property: JsonPropertyName("priorInsights")] IReadOnlyList<PriorInsightDto>? PriorInsights);

public sealed record PriorInsightDto(
    [property: JsonPropertyName("key")]        string Key,
    [property: JsonPropertyName("value")]      string Value,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("at")]         string At);

public sealed record RunnerResponse(
    [property: JsonPropertyName("ok")]    bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("plan")]  DeploymentPlanDto? Plan,
    [property: JsonPropertyName("verification")] VerificationDto? Verification,
    [property: JsonPropertyName("trace")] IReadOnlyList<AgentTraceDto>? Trace,
    [property: JsonPropertyName("remediation")] RemediationDto? Remediation);

public sealed record DeploymentPlanDto(
    [property: JsonPropertyName("prerequisites")] IReadOnlyList<string> Prerequisites,
    [property: JsonPropertyName("env")]           IReadOnlyDictionary<string, string> Environment,
    [property: JsonPropertyName("steps")]         IReadOnlyList<DeploymentStepDto> Steps,
    [property: JsonPropertyName("verifyHints")]   IReadOnlyList<string> VerifyHints)
{
    [property: JsonPropertyName("repoKind")]
    public string? RepoKind { get; init; }

    // Nullable on the wire so older clients that do not populate this
    // field (or explicitly send 'null') deserialise cleanly. Read back
    // through the <see cref="IsDeployableOrDefault"/> helper which
    // defaults a missing value to 'true' (the legacy behaviour).
    [property: JsonPropertyName("isDeployable")]
    public bool? IsDeployable { get; init; }

    [property: JsonPropertyName("notDeployableReason")]
    public string? NotDeployableReason { get; init; }

    public bool IsDeployableOrDefault => IsDeployable ?? true;
}

public sealed record DeploymentStepDto(
    [property: JsonPropertyName("id")]          int Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("cmd")]         string Command,
    [property: JsonPropertyName("cwd")]         string WorkingDirectory)
{
    /// <summary>
    /// Optional typed-action JSON. When non-null, the host orchestrator
    /// routes through the typed action layer instead of executing
    /// <see cref="Command"/> as bash. See
    /// <c>AgentStationHub.Services.Actions.IDeployAction</c> for the
    /// rationale. Null on legacy paths.
    /// </summary>
    [JsonPropertyName("actionJson")]
    public string? ActionJson { get; init; }
}

public sealed record VerificationDto(
    [property: JsonPropertyName("success")]  bool Success,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("notes")]    string? Notes);

public sealed record AgentTraceDto(
    [property: JsonPropertyName("agent")]   string Agent,
    [property: JsonPropertyName("stage")]   string Stage,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Proposed remediation for a failing step, produced by the
/// DeploymentDoctor agent. The orchestrator applies the remediation and
/// re-runs the plan, up to a bounded number of retries per session.
/// </summary>
public sealed record RemediationDto(
    // One of: "replace_step", "insert_before", "give_up"
    [property: JsonPropertyName("kind")]      string Kind,
    // Id of the failing step that triggered the remediation (for trace).
    [property: JsonPropertyName("stepId")]    int StepId,
    // New step(s) to insert or replace with. Empty when kind == "give_up".
    [property: JsonPropertyName("newSteps")]  IReadOnlyList<DeploymentStepDto>? NewSteps,
    // Human-readable rationale surfaced in the Live log.
    [property: JsonPropertyName("reasoning")] string? Reasoning);

