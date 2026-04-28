using System.Text.RegularExpressions;
using AgentStationHub.Models;

namespace AgentStationHub.Services.Security;

/// <summary>
/// Correctness guard (distinct from <see cref="PlanValidator"/>'s security
/// guard). Catches LLM-emitted commands that are syntactically valid and
/// security-clean but are known to either:
///   1. block forever / silently waste large amounts of wall-clock time
///      (e.g. <c>az resource wait --created</c> on a hardcoded name that
///      will never exist because the bicep template added a random
///      suffix), OR
///   2. not exist at all (e.g. <c>az acr wait</c>).
///
/// Each rule is keyed off a real failure observed in production. Rules
/// fire BEFORE the step ever reaches the sandbox, so we never waste a
/// 15-minute silence-watchdog cycle on a command that was structurally
/// guaranteed to hang. Rules apply to BOTH:
///   • the initial Strategist plan (via PlanValidator), and
///   • every Doctor remediation (replace_step / insert_before) before
///     the orchestrator splices the new steps in.
///
/// Each violation returns a short, machine-parseable <c>Code</c> plus a
/// human-readable <c>Reason</c>. The Doctor receives the Code in the
/// next remediation prompt as part of PREVIOUS_ATTEMPTS so it cannot
/// re-emit the same anti-pattern with cosmetic variations.
/// </summary>
public static class CommandSafetyGuard
{
    public sealed record Violation(string Code, string Reason);

    private static readonly (string Code, Regex Pattern, string Reason)[] Rules =
    [
        // 1) `az acr wait` does not exist as a subcommand. The Doctor
        //    occasionally emits this when trying to handle "Could not
        //    connect to the registry login server" — but `az acr` only
        //    has create/show/login/list/repository/task/build, no `wait`.
        //    The CLI prints "az acr: 'wait' is not in the 'az acr'
        //    command group" and the step exits non-zero, burning a
        //    Doctor invocation slot.
        ("AZ_ACR_WAIT_NOT_A_COMMAND",
            new Regex(@"\baz\s+acr\s+wait\b", RegexOptions.IgnoreCase),
            "`az acr wait` is not a valid Azure CLI subcommand. " +
            "Use `az acr show -n <name> -g <rg>` to verify existence " +
            "or query the actual registry name with " +
            "`az acr list -g <rg> --query \"[0].loginServer\" -o tsv`."),

        // 2) `az resource wait --created` (or `--exists`) on a literal
        //    --name that was NEVER created by a previous step. The
        //    canonical failure: bicep produced ACR
        //    'aiinvestacrtnggmxliptxjw' (random suffix), Doctor emitted
        //    `az resource wait --name aiinvest --created` — this hangs
        //    up to 60 minutes for a resource that will never exist.
        //
        //    Heuristic: --name argument is a bare literal (no $VAR, no
        //    $(...) substitution, no backticks) AND is shorter than 20
        //    chars. Real Azure resource names that survive Bicep
        //    `uniqueString()` mangling are almost always >=20 chars
        //    (12-char hash suffix on a 5-10 char namePrefix). Anything
        //    shorter is overwhelmingly a guess.
        //
        //    Allow-list: dynamic references (anything containing $).
        ("AZ_RESOURCE_WAIT_LITERAL_NAME",
            new Regex(
                @"\baz\s+resource\s+wait\b[^|;&]*--name\s+(?<n>[A-Za-z][A-Za-z0-9-]{0,18})\b(?![^\s]*\$)",
                RegexOptions.IgnoreCase),
            "`az resource wait --created` with a hardcoded short --name " +
            "is almost always wrong: most Bicep templates suffix names " +
            "with a 12-char `uniqueString` hash, so the literal name " +
            "will never exist and the command blocks for up to 60 min. " +
            "Resolve the real name first via `az <type> list -g <rg> " +
            "--query \"[0].name\" -o tsv` and reference it via $VAR."),

        // 3) `az group wait --created` / `az group wait --exists` on a
        //    resource group that the SAME plan has not yet created. The
        //    valid use is post-`az group create` to pause until ARM
        //    confirms; the broken use is "wait for an RG that I expect
        //    to exist" — when it doesn't, the command sits silent for
        //    its full 60min default. We don't block this outright (it
        //    has legitimate uses) but we tag it for visibility.
        //
        //    Implemented as a soft warning, NOT an error — see
        //    SoftRules below.

        // 4) `node <something>.js` where the file is a baked-in shell
        //    helper (no .js on disk, only an executable wrapper in
        //    /usr/local/bin). The Strategist sometimes wraps
        //    `relocate-node-modules` (a shell command) as `node
        //    relocate-node-modules.js`. Node then 404s with
        //    "Cannot find module".
        ("NODE_WRAPS_SHELL_HELPER",
            new Regex(
                @"\bnode\s+(?:\./)?(?<name>relocate-node-modules|relocate-venv|agentic-[a-z][a-z0-9-]*)" +
                @"(?:\.js)?\b",
                RegexOptions.IgnoreCase),
            "This is a baked shell helper, not a Node script. Invoke it " +
            "directly without `node` and without the `.js` suffix " +
            "(e.g. `relocate-node-modules /workspace`)."),

        // 5) Pipelines into shell that the security validator will
        //    reject anyway — surfaced earlier here with a friendlier
        //    message so the Doctor sees WHY it was rejected on the
        //    next round (PlanValidator alone says "Blacklisted pattern
        //    matched: \|\s*(sh|bash)\b").
        ("PIPE_TO_SHELL",
            new Regex(@"\|\s*(?:sh|bash)\b", RegexOptions.IgnoreCase),
            "Pipelines into `| sh` / `| bash` are blocked by the security " +
            "validator (RCE risk from any compromised upstream). Use a " +
            "here-string instead: `bash script.sh -g rg -l region <<< y`."),
    ];

    // Soft rules: log a warning, allow the step. Used for patterns that
    // are usually wrong but have legitimate edge cases.
    private static readonly (string Code, Regex Pattern, string Warning)[] SoftRules =
    [
        // Hardcoded *.azurecr.io with a SHORT prefix (<16 chars, no
        // random-looking suffix). Most Bicep templates suffix the
        // namePrefix with a 12-char uniqueString hash. A short literal
        // is almost certainly the namePrefix alone, which won't match
        // the real registry.
        ("ACR_LITERAL_SHORT_NAME",
            new Regex(@"(?<![A-Za-z0-9-])(?<n>[a-z0-9]{3,15})\.azurecr\.io\b",
                RegexOptions.IgnoreCase),
            "Hardcoded `<short>.azurecr.io` is suspicious — most Bicep " +
            "templates suffix namePrefix with a uniqueString hash. " +
            "Prefer resolving the registry at runtime: " +
            "`az acr list -g <rg> --query \"[0].loginServer\" -o tsv`."),
    ];

    /// <summary>
    /// Hard validation: returns null when clean, a violation when the
    /// command is structurally guaranteed to fail or hang. Caller
    /// should treat a non-null result as a rejection equivalent to
    /// PlanValidator.Validate returning ok=false.
    /// </summary>
    public static Violation? Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        foreach (var (code, pattern, reason) in Rules)
        {
            if (pattern.IsMatch(command))
                return new Violation(code, reason);
        }
        return null;
    }

    public static Violation? Validate(DeploymentStep step) => Validate(step.Command);

    public static Violation? Validate(IEnumerable<DeploymentStep> steps)
    {
        foreach (var s in steps)
        {
            var v = Validate(s);
            if (v is not null) return v with { Reason = $"Step {s.Id}: {v.Reason}" };
        }
        return null;
    }

    /// <summary>
    /// Soft scan: returns informational warnings without blocking. The
    /// caller logs them so the Strategist/Doctor can see in
    /// PREVIOUS_ATTEMPTS that a pattern was flagged and pivot.
    /// </summary>
    public static IReadOnlyList<Violation> SoftWarnings(string command)
    {
        var hits = new List<Violation>();
        if (string.IsNullOrWhiteSpace(command)) return hits;
        foreach (var (code, pattern, warning) in SoftRules)
        {
            if (pattern.IsMatch(command))
                hits.Add(new Violation(code, warning));
        }
        return hits;
    }
}
