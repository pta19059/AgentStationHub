using System.Text.RegularExpressions;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Static inspection of a cloned repository to detect which toolchains the
/// deployment will actually need (Node, Python, Go, Java, .NET, Bicep,
/// Terraform, Docker). The result feeds both the UI (so the user knows what
/// the sandbox must support) and the planner (so it can generate commands
/// that match the real project layout).
///
/// We look at three classes of signals:
///   1. Presence of language / framework marker files at common locations.
///   2. Contents of package manifests (scripts, dependencies).
///   3. Hooks declared in azure.yaml — these are the *actual* commands azd
///      will invoke during 'azd package' / 'azd deploy' and therefore the
///      strongest prerequisite signal.
/// </summary>
public static class RepoInspector
{
    public sealed record ToolchainManifest(
        bool Node,
        bool Python,
        bool Dotnet,
        bool Java,
        bool Go,
        bool Rust,
        bool Docker,
        bool Bicep,
        bool Terraform,
        bool Azd,
        IReadOnlyList<string> HookCommands,
        IReadOnlyList<string> Rationale)
    {
        public IEnumerable<string> Summary()
        {
            if (Node)      yield return "Node.js (npm/pnpm/yarn)";
            if (Python)    yield return "Python (pip/uv/poetry)";
            if (Dotnet)    yield return ".NET SDK";
            if (Java)      yield return "Java / Maven / Gradle";
            if (Go)        yield return "Go";
            if (Rust)      yield return "Rust / Cargo";
            if (Docker)    yield return "Docker build";
            if (Bicep)     yield return "Bicep";
            if (Terraform) yield return "Terraform";
            if (Azd)       yield return "Azure Developer CLI";
        }
    }

    public static ToolchainManifest Inspect(string repoRoot)
    {
        var rationale = new List<string>();
        bool Any(string pattern, int max = 3)
        {
            try
            {
                var matches = Directory.EnumerateFiles(repoRoot, pattern,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden
                    })
                    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                             && !p.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}")
                             && !p.Contains($"{Path.DirectorySeparatorChar}venv{Path.DirectorySeparatorChar}")
                             && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                    .Take(max)
                    .ToList();
                if (matches.Count == 0) return false;
                rationale.Add($"{pattern} -> {string.Join(", ",
                    matches.Select(m => Path.GetRelativePath(repoRoot, m)))}");
                return true;
            }
            catch { return false; }
        }

        bool node      = Any("package.json");
        bool python    = Any("pyproject.toml") | Any("requirements.txt") | Any("Pipfile") | Any("poetry.lock");
        bool dotnet    = Any("*.csproj")        | Any("*.fsproj")        | Any("global.json");
        bool java      = Any("pom.xml")         | Any("build.gradle")    | Any("build.gradle.kts");
        bool go        = Any("go.mod");
        bool rust      = Any("Cargo.toml");
        bool docker    = Any("Dockerfile")      | Any("docker-compose.yml") | Any("docker-compose.yaml");
        bool bicep     = Any("*.bicep");
        bool terraform = Any("*.tf");
        bool azd       = File.Exists(Path.Combine(repoRoot, "azure.yaml"))
                      || File.Exists(Path.Combine(repoRoot, "azure.yml"));
        if (azd) rationale.Add("azure.yaml found -> azd flow");

        var hookCmds = ExtractAzdHookCommands(repoRoot, rationale);

        // Hook contents can flip toolchain detection (e.g. hooks that shell out
        // to 'npm run build' in a repo without a root package.json).
        foreach (var h in hookCmds)
        {
            if (!node   && Regex.IsMatch(h, @"\b(npm|pnpm|yarn|npx)\b"))
            {
                node = true;
                rationale.Add($"azd hook references Node ({Truncate(h, 80)})");
            }
            if (!python && Regex.IsMatch(h, @"\b(pip|pip3|uv|poetry|python|python3)\b"))
            {
                python = true;
                rationale.Add($"azd hook references Python ({Truncate(h, 80)})");
            }
            if (!docker && Regex.IsMatch(h, @"\bdocker\b"))
            {
                docker = true;
                rationale.Add($"azd hook references Docker ({Truncate(h, 80)})");
            }
        }

        return new ToolchainManifest(
            Node: node, Python: python, Dotnet: dotnet, Java: java,
            Go: go, Rust: rust, Docker: docker, Bicep: bicep,
            Terraform: terraform, Azd: azd,
            HookCommands: hookCmds, Rationale: rationale);
    }

    private static IReadOnlyList<string> ExtractAzdHookCommands(
        string repoRoot, List<string> rationale)
    {
        // azure.yaml hook references can point to inline 'run:' strings OR to
        // external shell scripts via 'shell:' / 'windows:' / 'posix:' / 'path:'.
        // We collect the shell command text (inline or the file content) so the
        // planner can decide what tools the hooks actually invoke.
        var results = new List<string>();
        var azureYaml = Path.Combine(repoRoot, "azure.yaml");
        if (!File.Exists(azureYaml)) azureYaml = Path.Combine(repoRoot, "azure.yml");
        if (!File.Exists(azureYaml)) return results;

        string yaml;
        try { yaml = File.ReadAllText(azureYaml); }
        catch { return results; }

        // Inline run: | ... multi-line blocks.
        foreach (Match m in Regex.Matches(yaml,
            @"run\s*:\s*(?:\|[-+]?\s*\r?\n((?:[ \t]+.*\r?\n?)+)|""([^""]*)""|'([^']*)'|([^\r\n#]+))",
            RegexOptions.IgnoreCase))
        {
            var text = (m.Groups[1].Success ? m.Groups[1].Value :
                        m.Groups[2].Success ? m.Groups[2].Value :
                        m.Groups[3].Success ? m.Groups[3].Value :
                        m.Groups[4].Value).Trim();
            if (!string.IsNullOrWhiteSpace(text)) results.Add(text);
        }

        // External hook scripts: path: ./scripts/prebuild.sh
        foreach (Match m in Regex.Matches(yaml, @"path\s*:\s*([^\s#]+)", RegexOptions.IgnoreCase))
        {
            var rel = m.Groups[1].Value.Trim().Trim('"', '\'');
            var full = Path.GetFullPath(Path.Combine(repoRoot, rel));
            if (!full.StartsWith(Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(full)) continue;
            try
            {
                var content = File.ReadAllText(full);
                results.Add(content);
                rationale.Add($"azd hook script {rel} inspected");
            }
            catch { /* skip */ }
        }

        return results;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}
