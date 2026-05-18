using System.Text.RegularExpressions;
using AgentStationHub.Models;

namespace AgentStationHub.Services.Security;

public static class PlanValidator
{
    private static readonly HashSet<string> AllowedBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "azd", "az", "pac", "docker", "dotnet",
        "npm", "node", "pwsh", "python", "python3", "pip", "bash", "sh",
        "make", "terraform",
        // Harmless shell utilities the Strategist/Doctor frequently use
        // as Step 1 (status messages, no-op probes). Rejecting these
        // forces a full plan re-roll for what is essentially a print
        // statement.
        "echo", "true", "false", "test", "[",
        // v17 baked helpers in /usr/local/bin (see SandboxImageBuilder.cs).
        // Single-token, no nested quoting. The Strategist + Doctor prefer
        // these over hand-rolled shell. Adding them here makes the
        // validator accept the canonical helper-first plans.
        "relocate-node-modules", "relocate-venv",
        "agentic-help", "agentic-summary",
        "agentic-azd-up", "agentic-azd-env-prime",
        "agentic-azd-deploy",
        "agentic-acr-build", "agentic-build",
        "agentic-npm-install", "agentic-dotnet-restore",
        "agentic-bicep", "agentic-clone", "agentic-aca-wait",
    };

    private static readonly Regex[] Blacklist =
    [
        new(@"rm\s+-rf\s+/", RegexOptions.IgnoreCase),
        new(@"\|\s*(sh|bash)\b", RegexOptions.IgnoreCase),
        new(@"curl[^|]*\|\s*(sh|bash)", RegexOptions.IgnoreCase),
        new(@"Invoke-Expression|\biex\b", RegexOptions.IgnoreCase),
        new(@":\(\)\s*\{", RegexOptions.IgnoreCase),
        new(@"\b(shutdown|mkfs|dd\s+if=)\b", RegexOptions.IgnoreCase),
    ];

    // Detects bash -lc "..." with an unbalanced number of unescaped
    // double quotes inside the string body, which is the LLM-quoting
    // failure mode that produced 'mkdir: cannot create directory' for
    // an entire week. We use a generous threshold (>=8 escaped quotes)
    // so the rule only fires on truly pathological multi-level
    // nesting — moderately quote-heavy commands (e.g. az queries with
    // a couple of inline strings) are allowed through, since they
    // usually work fine when the Strategist emits them.
    private static (bool Ok, string? Reason) ValidateNoMultilevelQuoting(string cmd)
    {
        // Look for `bash -lc "..."` (or sh -c) and count `\"` escapes.
        var m = Regex.Match(cmd, @"\b(bash|sh)\s+-l?c\s+""", RegexOptions.IgnoreCase);
        if (!m.Success) return (true, null);
        int rest = m.Index + m.Length;
        int escapedQuotes = 0;
        for (int i = rest; i < cmd.Length - 1; i++)
            if (cmd[i] == '\\' && cmd[i + 1] == '"') escapedQuotes++;
        if (escapedQuotes >= 8)
            return (false,
                "Multi-level quoting detected inside bash -lc \"...\" (>=8 escaped quotes). " +
                "Use a baked helper (relocate-node-modules / agentic-*) or split the step.");
        return (true, null);
    }

    public static (bool Ok, string? Reason) Validate(DeploymentStep step)
    {
        var cmd = step.Command.Trim();
        var first = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!AllowedBinaries.Contains(first))
            return (false, $"Binary '{first}' not in whitelist.");

        foreach (var rx in Blacklist)
            if (rx.IsMatch(cmd))
                return (false, $"Blacklisted pattern matched: {rx}.");

        // Multi-quote heuristic: previously hard-rejected when >=8
        // escaped quotes were detected inside `bash -lc "..."`. In
        // practice the Strategist legitimately emits such commands
        // (e.g. `az ... --query "..." -o tsv | jq '..."x"...'`) and
        // rejecting the entire plan blocks the deploy with no recovery
        // path. If the command is actually malformed it will fail at
        // runtime and the Doctor will remediate. Kept as a soft check
        // (function preserved for potential future logging-only use).
        _ = ValidateNoMultilevelQuoting(cmd);

        if (step.WorkingDirectory.Contains("..") || Path.IsPathRooted(step.WorkingDirectory))
            return (false, "Working directory must be relative and inside workdir.");

        // Correctness guard (separate from the security blacklist above):
        // catches LLM-emitted patterns that are structurally guaranteed
        // to fail or hang (e.g. `az resource wait --created --name <hardcoded>`
        // for a resource that will never exist). See CommandSafetyGuard.
        var corr = CommandSafetyGuard.Validate(cmd);
        if (corr is not null)
            return (false, $"[{corr.Code}] {corr.Reason}");

        return (true, null);
    }

    public static (bool Ok, string? Reason) Validate(DeploymentPlan plan)
    {
        foreach (var s in plan.Steps)
        {
            var r = Validate(s);
            if (!r.Ok) return (false, $"Step {s.Id}: {r.Reason}");
        }
        return (true, null);
    }
}
