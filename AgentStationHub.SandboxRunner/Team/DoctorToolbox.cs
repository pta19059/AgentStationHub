using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentStationHub.SandboxRunner.Team;

/// <summary>
/// Tools the <c>DeploymentDoctor</c> agent can invoke autonomously during
/// its reasoning loop. They let the LLM INSPECT the real repo + the real
/// sandbox state instead of making decisions from an implicit model of
/// what an azd/docker project usually looks like.
///
/// Why this exists: in single-shot mode the Doctor has only the failure
/// tail + prior attempts to reason from. That's enough for well-known
/// signatures but useless for novel problems — the Doctor then resorts
/// to speculative sed/chmod/env-set remediation chains that spin the
/// orchestrator in circles. With tools the Doctor can:
///   1. Read the actual azure.yaml / Dockerfile / hook script that's
///      failing, instead of guessing its shape.
///   2. Run READ-ONLY diagnostics (docker info, az account show,
///      azd env get-values) to verify environment assumptions.
///   3. Check whether a file exists / a directory is populated before
///      proposing a fix that references it.
/// The tool set is intentionally small and read-mostly; write-side fixes
/// still go through the regular DeploymentStep pipeline so the
/// SecurityReviewer and orchestrator guardrails (DegradesDeploy,
/// near-duplicate detection) stay in the loop.
///
/// All tools execute inside the SAME sandbox container as the Doctor
/// agent, so /workspace and /usr/local/bin/* are the exact paths the
/// proposed remediations will see at execution time — there's no
/// "works on my machine" drift between diagnosis and fix.
/// </summary>
internal sealed class DoctorToolbox
{
    // Kept a bit wider than strictly necessary: the Doctor may legitimately
    // need to read a 10 KB Dockerfile or azure.yaml. Anything above is
    // almost certainly a runaway call (lock files, bundled CSS) and we
    // clip the tail so the LLM context stays manageable.
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxListEntries = 200;
    private const int MaxDiagnosticStdoutBytes = 16 * 1024;
    private const int DiagnosticTimeoutSeconds = 20;

    // Whitelist for run_diagnostic. Hard-coded: the Doctor cannot exec
    // anything that could mutate repo/cloud state through this channel.
    // If it wants to write, it goes through a remediation step reviewed
    // by the SecurityReviewer + DegradesDeploy guardrails.
    private static readonly HashSet<string> AllowedFirstTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // Filesystem introspection
        "ls", "cat", "head", "tail", "wc", "file", "stat", "find", "grep",
        "tree", "du", "df",
        // Runtime introspection
        "docker", "az", "azd", "dotnet", "node", "npm", "python3", "python",
        "pip", "uv", "git", "bicep", "terraform",
        "bash", "sh",     // allow 'bash -lc "docker ps"' pattern
        "which", "type", "echo", "pwd", "env", "printenv", "whoami",
        "uname"
    };

    // Further-forbidden substrings inside the ENTIRE command. These patterns
    // are write-side or destructive even when the first token is OK.
    private static readonly string[] ForbiddenSubstrings = new[]
    {
        ">", ">>",                 // redirection out (creates/overwrites files)
        "rm ", "rm\t",             // any rm
        "mv ",                     // file moves
        "cp ",                     // copy
        "chmod ", "chown ",        // perms change
        "sed -i", "awk -i",        // in-place edit
        "tee ",                    // writes
        "dd ",                     // bit copy
        "docker build", "docker run", "docker push", "docker rm",
        "docker rmi", "docker compose up", "docker compose down",
        "azd up", "azd deploy", "azd down", "azd provision",
        "azd env set",
        "az group delete", "az group create",
        "az deployment", "az account set",
        "npm install", "npm i ", "pip install",
        "dotnet restore", "dotnet build", "dotnet publish", "dotnet run",
        "curl ", "wget ",          // no network fetches (can exfil / install)
        "git checkout", "git reset", "git pull", "git push", "git clone",
        "|", "`", "$("             // block command substitution/pipes that
                                    // might sneak in a destructive sub-cmd
    };

    private readonly string _workspaceRoot;
    private readonly Action<string, string> _log;

    public DoctorToolbox(string workspaceRoot, Action<string, string> log)
    {
        _workspaceRoot = workspaceRoot;
        _log = log;
    }

    /// <summary>
    /// Materialise the tools as an <see cref="AIFunctionFactory"/>-produced
    /// list the Agent Framework can bind to a ChatClientAgent. Keeping the
    /// bundle here means the agent wiring in PlanningTeam stays a one-
    /// liner: <c>_chatClient.AsAIAgent(..., tools: toolbox.AsAITools())</c>.
    /// </summary>
    public IEnumerable<AITool> AsAITools() => new AITool[]
    {
        AIFunctionFactory.Create(
            ReadWorkspaceFile,
            name: "read_workspace_file",
            description:
                "Read a file under /workspace (the cloned repo). Use this to inspect " +
                "the actual contents of azure.yaml, Dockerfile, hook scripts, package.json, " +
                "global.json, infra/*.bicep, etc. BEFORE proposing a fix that modifies them. " +
                "Path is relative to the workspace root. Returns at most 64 KB; binary files " +
                "and paths outside /workspace are rejected."),
        AIFunctionFactory.Create(
            ListWorkspaceDirectory,
            name: "list_workspace_directory",
            description:
                "List files/directories matching a glob under /workspace. Use to find " +
                "Dockerfiles across a monorepo, hook scripts, azure.yaml in nested " +
                "folders, etc. Returns up to 200 entries. Path is relative to workspace root, " +
                "pattern is a glob like '**/Dockerfile' or 'infra/hooks/*.sh'."),
        AIFunctionFactory.Create(
            RunDiagnostic,
            name: "run_diagnostic",
            description:
                "Run a READ-ONLY diagnostic command inside the sandbox. Use to verify " +
                "environment assumptions: 'docker info', 'docker version', 'az account show', " +
                "'azd env get-values', 'dotnet --list-sdks', 'ls /workspace/.azure', etc. " +
                "Write-side commands are BLOCKED (rm, chmod, docker build, azd up, az group " +
                "delete, redirections, pipes). Command times out at 20 s. Stdout is trimmed " +
                "to 16 KB."),
        AIFunctionFactory.Create(
            CheckToolAvailable,
            name: "check_tool_available",
            description:
                "Check if a CLI tool is installed and runnable in the sandbox. Returns its " +
                "version string on success, an error message on failure. Saves a full " +
                "run_diagnostic round-trip when the Doctor just needs a yes/no on 'is " +
                "buildx here?'. Accepted tools: docker, buildx, az, azd, dotnet, node, " +
                "npm, python3, bicep, terraform, git, jq.")
    };

    // -----------------------------------------------------------------
    // Tool implementations
    // -----------------------------------------------------------------

    /// <summary>
    /// Read up to 64 KB of a file under /workspace. The Doctor uses this
    /// to inspect Dockerfile, azure.yaml, hook scripts, *.bicep, etc.
    /// before reasoning about a fix.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the workspace root (e.g. "azure.yaml",
    /// "infra/hooks/preprovision.sh", "src/api/Dockerfile").
    /// </param>
    public string ReadWorkspaceFile(string relativePath)
    {
        try
        {
            var resolved = ResolveUnderWorkspace(relativePath);
            if (resolved is null) return "ERROR: path escapes /workspace";
            if (!File.Exists(resolved)) return $"ERROR: file not found: {relativePath}";

            var fi = new FileInfo(resolved);
            var clipped = fi.Length > MaxFileBytes;
            using var stream = File.OpenRead(resolved);
            var buf = new byte[Math.Min(MaxFileBytes, (int)fi.Length)];
            int read = stream.Read(buf, 0, buf.Length);
            // Quick binary sniff — abort if many non-printable bytes in
            // the first 512 to avoid flooding the LLM with garbage.
            int sniffLen = Math.Min(read, 512);
            int nonPrintable = 0;
            for (int i = 0; i < sniffLen; i++)
            {
                byte b = buf[i];
                if (b == 0 || (b < 32 && b != 9 && b != 10 && b != 13)) nonPrintable++;
            }
            if (sniffLen > 0 && (double)nonPrintable / sniffLen > 0.2)
                return $"ERROR: {relativePath} appears to be binary ({fi.Length} bytes).";

            var text = Encoding.UTF8.GetString(buf, 0, read);
            _log("info", $"[Doctor] read_workspace_file('{relativePath}') -> {read} bytes" +
                         (clipped ? " (clipped)" : ""));
            return clipped
                ? text + $"\n\n...[clipped at {MaxFileBytes} bytes of {fi.Length}]"
                : text;
        }
        catch (Exception ex)
        {
            return $"ERROR reading '{relativePath}': {ex.GetType().Name} {ex.Message}";
        }
    }

    /// <summary>
    /// List up to 200 entries matching a glob pattern under /workspace.
    /// Helps the Doctor find files whose location it doesn't know a
    /// priori — e.g. "where is the preprovision hook script declared by
    /// azure.yaml?", "do any Dockerfiles in this monorepo use BuildKit?".
    /// </summary>
    /// <param name="relativePath">
    /// Starting directory, relative to workspace root. Empty or "." =
    /// workspace root.
    /// </param>
    /// <param name="pattern">
    /// Glob pattern, e.g. "*.bicep", "**/Dockerfile", "infra/hooks/*.sh".
    /// Defaults to "*" if not provided.
    /// </param>
    public string ListWorkspaceDirectory(string relativePath, string pattern)
    {
        try
        {
            var startDir = string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
                ? _workspaceRoot
                : ResolveUnderWorkspace(relativePath);
            if (startDir is null) return "ERROR: path escapes /workspace";
            if (!Directory.Exists(startDir)) return $"ERROR: directory not found: {relativePath}";

            pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;

            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(pattern);
            var results = matcher.Execute(
                new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                    new DirectoryInfo(startDir)));

            var lines = results.Files
                .Select(f => f.Path.Replace('\\', '/'))
                .Take(MaxListEntries)
                .ToList();

            _log("info", $"[Doctor] list_workspace_directory('{relativePath}', '{pattern}') -> " +
                         $"{lines.Count} entries");
            if (lines.Count == 0) return $"(no entries under '{relativePath}' matching '{pattern}')";
            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            if (results.Files.Count() > MaxListEntries)
                sb.AppendLine($"...[truncated at {MaxListEntries} entries]");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ERROR listing '{relativePath}' with '{pattern}': {ex.GetType().Name} {ex.Message}";
        }
    }

    /// <summary>
    /// Run a read-only diagnostic command inside the sandbox. Heavily
    /// sandboxed: whitelisted first token + blocklisted destructive
    /// substrings + 20 s timeout + 16 KB stdout cap.
    /// </summary>
    /// <param name="command">
    /// Command to execute (e.g. "docker info", "az account show",
    /// "azd env get-values", "dotnet --list-sdks"). Will be run via
    /// <c>bash -lc</c> so shell features like quoting work.
    /// </param>
    public string RunDiagnostic(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "ERROR: empty command";

        // First-token check.
        var firstToken = command.TrimStart().Split(' ', 2)[0].Trim();
        if (!AllowedFirstTokens.Contains(firstToken))
        {
            return $"ERROR: '{firstToken}' is not on the read-only tool allowlist. " +
                   $"Use run_diagnostic only for inspection (ls/cat/docker info/az account show/...).";
        }

        // Substring blocklist for write / destructive operations even
        // behind allowed first tokens (e.g. 'docker build', 'bash -lc "rm ..."').
        foreach (var bad in ForbiddenSubstrings)
        {
            if (command.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                return $"ERROR: command rejected — contains forbidden token '{bad.Trim()}'. " +
                       "run_diagnostic is READ-ONLY; propose a remediation step if a write is " +
                       "actually required.";
            }
        }

        try
        {
            using var p = new Process
            {
                StartInfo =
                {
                    FileName = "bash",
                    Arguments = $"-lc \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workspaceRoot
                }
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(DiagnosticTimeoutSeconds * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* race */ }
                return $"ERROR: diagnostic timed out after {DiagnosticTimeoutSeconds}s. " +
                       "Prefer a narrower command (e.g. 'docker version' over 'docker info').";
            }

            var outStr = TrimTo(stdout.ToString(), MaxDiagnosticStdoutBytes);
            var errStr = TrimTo(stderr.ToString(), MaxDiagnosticStdoutBytes / 2);
            _log("info", $"[Doctor] run_diagnostic('{Trim(command, 80)}') exit={p.ExitCode}");
            var result = new StringBuilder();
            result.AppendLine($"exit: {p.ExitCode}");
            if (outStr.Length > 0) { result.AppendLine("stdout:"); result.AppendLine(outStr); }
            if (errStr.Length > 0) { result.AppendLine("stderr:"); result.AppendLine(errStr); }
            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"ERROR running diagnostic: {ex.GetType().Name} {ex.Message}";
        }
    }

    /// <summary>
    /// Quick yes/no check for a specific tool's availability + version.
    /// Saves the Doctor a heavier run_diagnostic round-trip when it just
    /// needs to know "is 'buildx' available here?" before proposing a fix.
    /// </summary>
    /// <param name="tool">
    /// One of: docker, buildx, az, azd, dotnet, node, npm, python3, bicep,
    /// terraform, git, jq. Other values are rejected.
    /// </param>
    public string CheckToolAvailable(string tool)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker"]    = "docker --version",
            ["buildx"]    = "docker buildx version",
            ["az"]        = "az --version",
            ["azd"]       = "azd version",
            ["dotnet"]    = "dotnet --list-sdks",
            ["node"]      = "node --version",
            ["npm"]       = "npm --version",
            ["python3"]   = "python3 --version",
            ["bicep"]     = "az bicep version",
            ["terraform"] = "terraform version",
            ["git"]       = "git --version",
            ["jq"]        = "jq --version"
        };
        if (!known.TryGetValue(tool, out var probeCmd))
            return $"ERROR: '{tool}' is not in the known-tools set. " +
                   $"Known: {string.Join(", ", known.Keys)}.";
        return RunDiagnostic(probeCmd);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Resolve <paramref name="relativePath"/> against the workspace root
    /// and reject anything that escapes via '..', absolute paths, or
    /// symlinks pointing outside /workspace. Returns null on escape.
    /// </summary>
    private string? ResolveUnderWorkspace(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return _workspaceRoot;

        // Strip any leading '/' the LLM might have added by habit.
        relativePath = relativePath.TrimStart('/');

        var root = Path.GetFullPath(_workspaceRoot);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!combined.StartsWith(root, StringComparison.Ordinal))
            return null;
        return combined;
    }

    private static string TrimTo(string s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes <= maxBytes) return s;
        var tail = s.Substring(Math.Max(0, s.Length - maxBytes / 2));
        return $"[clipped {bytes - maxBytes} bytes]\n...{tail}";
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
