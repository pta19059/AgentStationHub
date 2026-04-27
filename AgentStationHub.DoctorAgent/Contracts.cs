using System.Text.Json.Serialization;

namespace AgentStationHub.DoctorAgent;

// ---------------------------------------------------------------------------
// Wire DTOs � MUST match the host's contract in:
//   AgentStationHub/Services/Tools/SandboxRunnerHost.cs (RunnerRequest et al.)
//   AgentStationHub.SandboxRunner/Contracts/RunnerContracts.cs
// We duplicate them here on purpose to keep the Hosted Agent self-contained.
// If a field is added on the host side, mirror it here. Backward-compatible
// fields default to null so older hosts can still call this agent.
// ---------------------------------------------------------------------------

/// <summary>
/// Subset of the runner protocol that this Hosted Agent understands.
/// We only handle the "remediate" command � plan/verify stay on the host.
/// </summary>
public sealed class DoctorRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "remediate";

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("plan")]
    public PlanDto? Plan { get; set; }

    [JsonPropertyName("failedStepId")]
    public int? FailedStepId { get; set; }

    [JsonPropertyName("errorTail")]
    public string? ErrorTail { get; set; }

    [JsonPropertyName("previousAttempts")]
    public List<string>? PreviousAttempts { get; set; }

    [JsonPropertyName("priorInsights")]
    public List<PriorInsight>? PriorInsights { get; set; }

    /// <summary>
    /// Pre-bundled repo-file slices the host orchestrator decided to ship.
    /// Replaces the in-sandbox DoctorToolbox.read_workspace_file tool: when
    /// running as a Hosted Agent we do not have access to the host's
    /// /var/agentichub-work volume, so the host pre-reads the most likely
    /// relevant files and includes them here.
    ///
    /// Map: relative-path -> file content (truncated by the host to 64 KB).
    /// May be null on legacy host calls; the Doctor still produces a
    /// best-effort remediation in that case (just blind to file contents).
    /// </summary>
    [JsonPropertyName("repoFiles")]
    public Dictionary<string, string>? RepoFiles { get; set; }
}

public sealed class DoctorResponse
{
    [JsonPropertyName("ok")]          public bool Ok { get; set; }
    [JsonPropertyName("error")]       public string? Error { get; set; }
    [JsonPropertyName("remediation")] public RemediationDto? Remediation { get; set; }
    [JsonPropertyName("trace")]       public List<TraceLine>? Trace { get; set; }
}

public sealed class TraceLine
{
    [JsonPropertyName("agent")]   public string Agent { get; set; } = "";
    [JsonPropertyName("stage")]   public string Stage { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class PlanDto
{
    [JsonPropertyName("prerequisites")] public List<string>? Prerequisites { get; set; }
    [JsonPropertyName("env")]           public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("steps")]         public List<StepDto>? Steps { get; set; }
    [JsonPropertyName("verifyHints")]   public List<string>? VerifyHints { get; set; }
    [JsonPropertyName("repoKind")]      public string? RepoKind { get; set; }
}

public sealed class StepDto
{
    [JsonPropertyName("id")]          public int Id { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("cmd")]         public string Cmd { get; set; } = "";
    [JsonPropertyName("cwd")]         public string Cwd { get; set; } = ".";
    [JsonPropertyName("actionJson")]  public string? ActionJson { get; set; }
}

public sealed class RemediationDto
{
    /// <summary>One of "replace_step" | "insert_before" | "give_up".</summary>
    [JsonPropertyName("kind")]      public string Kind { get; set; } = "give_up";
    [JsonPropertyName("stepId")]    public int StepId { get; set; }
    [JsonPropertyName("newSteps")]  public List<StepDto>? NewSteps { get; set; }
    [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
}

public sealed class PriorInsight
{
    [JsonPropertyName("key")]        public string Key { get; set; } = "";
    [JsonPropertyName("value")]      public string Value { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("at")]         public string At { get; set; } = "";
}
