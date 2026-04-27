using System.Text.RegularExpressions;
using AgentStationHub.Models;

namespace AgentStationHub.Services.Security;

public static class PlanValidator
{
    private static readonly HashSet<string> AllowedBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "azd", "az", "pac", "docker", "dotnet",
        "npm", "node", "pwsh", "python", "pip", "bash", "sh",
        "make", "terraform",
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
    // an entire week. If we count more than ONE level of nested escape
    // in a `bash -lc "..."` payload, we reject the step. The Strategist
    // and Doctor are pushed toward baked helpers instead.
    private static (bool Ok, string? Reason) ValidateNoMultilevelQuoting(string cmd)
    {
        // Look for `bash -lc "..."` (or sh -c) and count `\"` escapes.
        var m = Regex.Match(cmd, @"\b(bash|sh)\s+-l?c\s+""", RegexOptions.IgnoreCase);
        if (!m.Success) return (true, null);
        int rest = m.Index + m.Length;
        int escapedQuotes = 0;
        for (int i = rest; i < cmd.Length - 1; i++)
            if (cmd[i] == '\\' && cmd[i + 1] == '"') escapedQuotes++;
        if (escapedQuotes >= 4)
            return (false,
                "Multi-level quoting detected inside bash -lc \"...\" (>=4 escaped quotes). " +
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

        var quoteCheck = ValidateNoMultilevelQuoting(cmd);
        if (!quoteCheck.Ok) return quoteCheck;

        if (step.WorkingDirectory.Contains("..") || Path.IsPathRooted(step.WorkingDirectory))
            return (false, "Working directory must be relative and inside workdir.");

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
