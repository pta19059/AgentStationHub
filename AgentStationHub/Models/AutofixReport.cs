namespace AgentStationHub.Models;

public sealed record AutofixReport(
    string SessionId,
    string ResourceGroup,
    string RepoUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<AutofixCheck> Checks,
    string Summary)
{
    public int TotalChecks => Checks.Count;
    public int Fixed => Checks.Count(c => c.Outcome == AutofixOutcome.Fixed);
    public int AlreadyHealthy => Checks.Count(c => c.Outcome == AutofixOutcome.Ok);
    public int FailedToFix => Checks.Count(c => c.Outcome == AutofixOutcome.FailedToFix);
}

public sealed record AutofixCheck(
    string Category,
    string Target,
    AutofixOutcome Outcome,
    string Details,
    string? FixCommand,
    string? FixOutput);

public enum AutofixOutcome
{
    Ok,
    Fixed,
    FailedToFix,
    Skipped
}
