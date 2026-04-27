using System.Text.RegularExpressions;

namespace AgentStationHub.SandboxRunner.Inspection;

/// <summary>
/// Deterministic repository scanner. Runs inside the sandbox with direct
/// access to /workspace (no file copies). Produces a structured manifest of
/// toolchains and hook commands that the agent team uses as ground truth.
/// </summary>
public static class RepoInspector
{
    public sealed record ToolchainManifest(
        bool Node, bool Python, bool Dotnet, bool Java, bool Go, bool Rust,
        bool Docker, bool Bicep, bool Terraform, bool Azd,
        IReadOnlyList<string> HookCommands,
        IReadOnlyList<string> Rationale,
        string? Readme,
        IReadOnlyDictionary<string, string> KeyFileContents,
        IReadOnlyList<string> InfraFiles,
        IReadOnlyList<string> AzdRequiredEnvVars,
        // Education / content signals used by the classifier gate to tell
        // a deployable app from a course / tutorial / docs site.
        int NotebookCount,
        int LessonFolderCount,
        bool HasInfrastructureAsCode,
        bool HasDeploymentEntry,
        // All env vars referenced by the parameters file, INCLUDING the
        // ones with a Bicep default ('${VAR=default}'). The Doctor needs
        // these for 'azd env set' remediations — if the template reads
        // the model name / version from an env var with a default, a
        // 'sed' on the .bicep file does NOT fix the deploy (parameters
        // takes precedence). Each entry is "VAR_NAME=current_default"
        // so the Doctor can see what the default is.
        IReadOnlyList<string> AzdAllEnvVars,
        // ?? Toolchain-pin awareness (used by the Strategist / Doctor to
        // avoid the 10+ attempts we've seen when a repo's global.json
        // pins a .NET SDK version that is not present in the sandbox
        // image). Null when no global.json is found.
        //
        // DotnetSdkPin           : the "version" field verbatim (e.g. "10.0.100").
        // DotnetSdkPinMajor      : first dotted component parsed as int, or null.
        // DotnetSdkPinSatisfiable: true if the sandbox image carries this major
        //                          (currently baked at SDK 8 + SDK 9). When
        //                          false the plan MUST include a 'relax
        //                          global.json' step before any 'dotnet'
        //                          invocation, otherwise every 'azd package'
        //                          or 'dotnet user-secrets' call will fail
        //                          with 'No .NET SDKs were found'.
        string? DotnetSdkPin,
        int? DotnetSdkPinMajor,
        bool DotnetSdkPinSatisfiable)
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

        bool Any(string pattern)
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
                    .Take(3).ToList();
                if (matches.Count == 0) return false;
                rationale.Add($"{pattern} -> {string.Join(", ",
                    matches.Select(m => Path.GetRelativePath(repoRoot, m)))}");
                return true;
            }
            catch { return false; }
        }

        bool node      = Any("package.json");
        bool python    = Any("pyproject.toml") | Any("requirements.txt") | Any("Pipfile");
        bool dotnet    = Any("*.csproj")  | Any("*.fsproj")  | Any("global.json");
        bool java      = Any("pom.xml")   | Any("build.gradle") | Any("build.gradle.kts");
        bool go        = Any("go.mod");
        bool rust      = Any("Cargo.toml");
        bool docker    = Any("Dockerfile") | Any("docker-compose.yml") | Any("docker-compose.yaml");
        bool bicep     = Any("*.bicep");
        bool terraform = Any("*.tf");
        bool azd       = File.Exists(Path.Combine(repoRoot, "azure.yaml"))
                      || File.Exists(Path.Combine(repoRoot, "azure.yml"));
        if (azd) rationale.Add("azure.yaml found -> azd flow");

        var hookCmds = ExtractAzdHookCommands(repoRoot, rationale);

        foreach (var h in hookCmds)
        {
            if (!node   && Regex.IsMatch(h, @"\b(npm|pnpm|yarn|npx)\b"))
            { node = true; rationale.Add($"azd hook references Node"); }
            if (!python && Regex.IsMatch(h, @"\b(pip|pip3|uv|poetry|python|python3)\b"))
            { python = true; rationale.Add($"azd hook references Python"); }
            if (!docker && Regex.IsMatch(h, @"\bdocker\b"))
            { docker = true; rationale.Add($"azd hook references Docker"); }
        }

        var readme = ReadText(Path.Combine(repoRoot, "README.md"), 60_000)
                  ?? ReadText(Path.Combine(repoRoot, "readme.md"), 60_000);

        var keyFiles = new Dictionary<string, string?>
        {
            ["azure.yaml"]         = ReadText(Path.Combine(repoRoot, "azure.yaml")) ?? ReadText(Path.Combine(repoRoot, "azure.yml")),
            ["Dockerfile"]         = ReadText(Path.Combine(repoRoot, "Dockerfile")),
            ["docker-compose.yml"] = ReadText(Path.Combine(repoRoot, "docker-compose.yml")) ?? ReadText(Path.Combine(repoRoot, "docker-compose.yaml")) ?? ReadText(Path.Combine(repoRoot, "compose.yaml")),
            ["package.json"]       = ReadText(Path.Combine(repoRoot, "package.json"), 4_000),
            ["pyproject.toml"]     = ReadText(Path.Combine(repoRoot, "pyproject.toml"), 4_000),
            ["requirements.txt"]   = ReadText(Path.Combine(repoRoot, "requirements.txt"), 4_000),
            ["Makefile"]           = ReadText(Path.Combine(repoRoot, "Makefile"), 4_000),
            ["main.bicep"]         = ReadText(Path.Combine(repoRoot, "infra", "main.bicep"), 4_000),
            ["main.tf"]            = ReadText(Path.Combine(repoRoot, "infra", "main.tf"), 4_000),
        };
        var presentFiles = keyFiles.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                                   .ToDictionary(kv => kv.Key, kv => kv.Value!);

        var infra = ListFiles(Path.Combine(repoRoot, "infra"))
            .Concat(ListFiles(repoRoot, topOnly: true, "azure.yaml", "azure.yml"))
            .Distinct().ToList();

        // azd's 'infra/main.parameters.json' references ${AZURE_XXX} env vars
        // for every required provisioning input. If any are missing at 'azd
        // up' time, provisioning fails in non-interactive mode. The
        // Strategist needs to know these to generate 'azd env set' steps.
        var azdRequired = ExtractAzdRequiredEnvVars(repoRoot);
        if (azdRequired.Count > 0)
            rationale.Add($"azd required env vars: {string.Join(", ", azdRequired)}");

        // ALL env vars (required + optional-with-default). Used by the
        // Doctor to know which remediations should be 'azd env set'
        // rather than 'sed' on Bicep — critical for model name / version
        // fixes where the .bicep reads from parameters.json.
        var azdAll = ExtractAllAzdEnvVars(repoRoot);
        if (azdAll.Count > azdRequired.Count)
            rationale.Add(
                $"azd optional env vars (with defaults): " +
                string.Join(", ", azdAll.Where(v => !azdRequired.Any(r => v.StartsWith(r + "=") || v == r))));

        // ---- Education / deployability signals ----
        // Count Jupyter notebooks (strong indicator of a course / tutorial).
        var notebookCount = SafeCount(repoRoot, "*.ipynb");

        // Folders named 'lessons', 'labs', 'exercises', 'tutorials',
        // 'course', 'chapterN' are typical of curricula.
        var lessonFolderCount = SafeCountDirs(repoRoot, rel =>
            Regex.IsMatch(rel,
                @"(^|/)(lessons?|labs?|exercises?|tutorials?|courses?|chapter\d+|session\d+|modules?)(/|$)",
                RegexOptions.IgnoreCase));

        var hasIac = bicep || terraform ||
                     File.Exists(Path.Combine(repoRoot, "infra", "main.bicep")) ||
                     File.Exists(Path.Combine(repoRoot, "infra", "main.tf"));

        // Consider the repo a "deployment entry" if ANY of: azure.yaml
        // present, docker-compose.yml, Dockerfile + an IaC, or npm
        // 'deploy' script. These are the explicit "this is meant to be
        // deployed" signatures.
        var hasComposeFile = File.Exists(Path.Combine(repoRoot, "docker-compose.yml"))
                          || File.Exists(Path.Combine(repoRoot, "docker-compose.yaml"));
        var hasNpmDeploy = presentFiles.TryGetValue("package.json", out var pkg) &&
                           pkg.Contains("\"deploy\"", StringComparison.OrdinalIgnoreCase);
        var hasDeploymentEntry = azd || hasComposeFile || (docker && hasIac) || hasNpmDeploy;

        if (notebookCount > 0)
            rationale.Add($"notebooks: {notebookCount} .ipynb file(s)");
        if (lessonFolderCount > 0)
            rationale.Add($"lesson/course folders: {lessonFolderCount}");
        if (!hasDeploymentEntry)
            rationale.Add("no deployment entrypoint found (azure.yaml / docker-compose / Dockerfile+IaC / npm run deploy)");

        // global.json SDK pin analysis — detect + classify BEFORE the
        // plan is generated so the Strategist can bake a "relax the pin"
        // step into the first attempt rather than letting the Doctor
        // discover the problem after azd fails 3+ times.
        var (sdkPin, sdkMajor, sdkOk) = InspectDotnetSdkPin(repoRoot);
        if (sdkPin is not null)
        {
            var satisfiability = sdkOk
                ? "satisfiable by sandbox"
                : "NOT satisfiable (sandbox has SDK 8 + SDK 9 only)";
            rationale.Add($"global.json SDK pin: {sdkPin} ({satisfiability})");
        }

        return new ToolchainManifest(
            node, python, dotnet, java, go, rust, docker, bicep, terraform, azd,
            hookCmds, rationale, readme, presentFiles, infra, azdRequired,
            notebookCount, lessonFolderCount, hasIac, hasDeploymentEntry,
            azdAll,
            sdkPin, sdkMajor, sdkOk);
    }

    /// <summary>
    /// Read <c>global.json</c> at the repo root and classify the SDK
    /// version pin against what the sandbox image ships.
    ///
    /// Returns (pin, major, satisfiable) where:
    ///   pin         : the literal "version" string, or null when no
    ///                 global.json / no sdk.version field exists.
    ///   major       : first dotted component as int (e.g. "10.0.100" ? 10),
    ///                 or null when unparseable.
    ///   satisfiable : true iff the sandbox image provides this major
    ///                 version. The sandbox currently bakes SDK 8 + 9;
    ///                 any pin in [8..9] is OK, anything else isn't.
    ///
    /// When global.json lacks a "sdk.version" field entirely (only
    /// "rollForward" or similar), the pin is null and satisfiable
    /// defaults to true (no constraint to worry about).
    /// </summary>
    private static (string? Pin, int? Major, bool Satisfiable) InspectDotnetSdkPin(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "global.json");
        if (!File.Exists(path)) return (null, null, true);

        try
        {
            var content = File.ReadAllText(path);
            // Lightweight regex rather than System.Text.Json — a malformed
            // global.json (trailing comma, comments) should NOT crash the
            // inspection; we try-best-effort instead.
            var m = Regex.Match(content,
                "\"version\"\\s*:\\s*\"([0-9]+)\\.([0-9]+)\\.([0-9]+)\"",
                RegexOptions.Singleline);
            if (!m.Success) return (null, null, true);

            var pin = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
            var major = int.Parse(m.Groups[1].Value);

            // Keep this list in sync with SandboxImageBuilder.Dockerfile —
            // adding SDK 10 there means adding 10 here.
            var sandboxMajors = new[] { 8, 9 };
            var satisfiable = sandboxMajors.Contains(major);
            return (pin, major, satisfiable);
        }
        catch
        {
            // Unreadable or malformed global.json — let the Doctor deal
            // with whatever error azd surfaces. Pretend it doesn't exist.
            return (null, null, true);
        }
    }

    private static int SafeCount(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
              .Take(5000)
              .Count();
        }
        catch { return 0; }
    }

    private static int SafeCountDirs(string root, Func<string, bool> relPredicate)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
              .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
              .Count(relPredicate);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Enumerates EVERY env var referenced by the parameters file, including
    /// those with a Bicep-side default (<c>${VAR=default}</c>) and those
    /// whose bicepparam 'readEnvironmentVariable' call has a default. Each
    /// result entry has the form <c>VAR_NAME=default_value</c> when a
    /// default exists, or plain <c>VAR_NAME</c> otherwise.
    ///
    /// This is strictly a SUPERSET of <see cref="ExtractAzdRequiredEnvVars"/>.
    /// The Doctor consumes it to know that a "change model version" fix
    /// should be an <c>azd env set OPENAI_VERSION ...</c> rather than a
    /// <c>sed</c> on <c>*.bicep</c> — otherwise the deploy keeps reading
    /// the stale default from <c>main.parameters.json</c>.
    /// </summary>
    private static IReadOnlyList<string> ExtractAllAzdEnvVars(string repoRoot)
    {
        var results = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in new[]
        {
            Path.Combine(repoRoot, "infra", "main.parameters.json"),
            Path.Combine(repoRoot, "infra", "main.bicepparam"),
        })
        {
            if (!File.Exists(path)) continue;
            string content;
            try { content = File.ReadAllText(path); } catch { continue; }

            // JSON without default: "value": "${VAR}"
            foreach (Match m in Regex.Matches(content,
                @"""value""\s*:\s*""\$\{\s*([A-Z][A-Z0-9_]+)\s*\}"""))
            {
                var v = m.Groups[1].Value;
                if (!results.ContainsKey(v)) results[v] = null;
            }
            // JSON with default: "value": "${VAR=default}"
            foreach (Match m in Regex.Matches(content,
                @"""value""\s*:\s*""\$\{\s*([A-Z][A-Z0-9_]+)\s*=\s*([^}]*)\s*\}"""))
            {
                results[m.Groups[1].Value] = m.Groups[2].Value;
            }
            // bicepparam: readEnvironmentVariable('VAR')
            foreach (Match m in Regex.Matches(content,
                @"readEnvironmentVariable\(\s*'([A-Z][A-Z0-9_]+)'\s*\)",
                RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value;
                if (!results.ContainsKey(v)) results[v] = null;
            }
            // bicepparam with default: readEnvironmentVariable('VAR', 'default')
            foreach (Match m in Regex.Matches(content,
                @"readEnvironmentVariable\(\s*'([A-Z][A-Z0-9_]+)'\s*,\s*'([^']*)'\s*\)",
                RegexOptions.IgnoreCase))
            {
                results[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        string[] ambient = {
            "AZURE_SUBSCRIPTION_ID", "AZURE_TENANT_ID",
            "AZURE_ENV_NAME", "AZURE_PRINCIPAL_ID", "AZURE_RESOURCE_GROUP"
        };

        return results
            .Where(kv => !ambient.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}")
            .ToList();
    }

    private static IReadOnlyList<string> ExtractAzdRequiredEnvVars(string repoRoot)
    {
        // Step 1: map every Bicep param name -> env var name by scanning the
        // parameters files. '${VAR=default}' counts as optional (skipped via
        // the negative lookahead). For bicepparam, a readEnvironmentVariable
        // call without a default argument counts as required.
        var paramToEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[]
        {
            Path.Combine(repoRoot, "infra", "main.parameters.json"),
            Path.Combine(repoRoot, "infra", "main.bicepparam"),
        })
        {
            if (!File.Exists(path)) continue;
            string content;
            try { content = File.ReadAllText(path); } catch { continue; }

            // JSON:  "paramName": { "value": "${ENV_VAR}" }
            foreach (Match m in Regex.Matches(content,
                "\"([A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*\\{\\s*\"value\"\\s*:\\s*\"\\$\\{\\s*([A-Z][A-Z0-9_]+)(?![=:])\\s*\\}\""))
            {
                paramToEnv[m.Groups[1].Value] = m.Groups[2].Value;
            }
            // bicepparam: 'param X = readEnvironmentVariable('VAR')' (no default).
            foreach (Match m in Regex.Matches(content,
                @"param\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*readEnvironmentVariable\(\s*'([A-Z][A-Z0-9_]+)'\s*\)",
                RegexOptions.IgnoreCase))
            {
                paramToEnv[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        if (paramToEnv.Count == 0) return Array.Empty<string>();

        // Step 2: scan main.bicep to identify params WITHOUT a default value.
        // 'param X T'        -> required
        // 'param X T = expr' -> optional (Bicep default is used if env unset)
        var requiredBicepParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bicep in new[]
        {
            Path.Combine(repoRoot, "infra", "main.bicep"),
            Path.Combine(repoRoot, "main.bicep")
        })
        {
            if (!File.Exists(bicep)) continue;
            string content;
            try { content = File.ReadAllText(bicep); } catch { continue; }

            foreach (Match m in Regex.Matches(content,
                @"^[ \t]*param[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]+[^\r\n=]+?(=|\r?\n|$)",
                RegexOptions.Multiline))
            {
                if (m.Groups[2].Value != "=")
                    requiredBicepParams.Add(m.Groups[1].Value);
            }
        }

        // Cross-reference: emit only env vars bound to a required Bicep param.
        // If Bicep could not be read, fall back to LOCATION/REGION vars only
        // — these rarely have sensible defaults and are universal across
        // azd templates.
        IEnumerable<KeyValuePair<string, string>> pairs = requiredBicepParams.Count > 0
            ? paramToEnv.Where(kv => requiredBicepParams.Contains(kv.Key))
            : paramToEnv.Where(kv =>
                kv.Value.Contains("LOCATION", StringComparison.OrdinalIgnoreCase)
             || kv.Value.Contains("REGION", StringComparison.OrdinalIgnoreCase));

        return pairs
            .Select(kv => kv.Value)
            .Where(v => v is not "AZURE_SUBSCRIPTION_ID" and not "AZURE_TENANT_ID"
                        and not "AZURE_ENV_NAME" and not "AZURE_LOCATION"
                        and not "AZURE_PRINCIPAL_ID" and not "AZURE_RESOURCE_GROUP")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractAzdHookCommands(string repoRoot, List<string> rationale)
    {
        var results = new List<string>();
        var azureYaml = Path.Combine(repoRoot, "azure.yaml");
        if (!File.Exists(azureYaml)) azureYaml = Path.Combine(repoRoot, "azure.yml");
        if (!File.Exists(azureYaml)) return results;

        string yaml;
        try { yaml = File.ReadAllText(azureYaml); }
        catch { return results; }

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

        foreach (Match m in Regex.Matches(yaml, @"path\s*:\s*([^\s#]+)", RegexOptions.IgnoreCase))
        {
            var rel = m.Groups[1].Value.Trim().Trim('"', '\'');
            var full = Path.GetFullPath(Path.Combine(repoRoot, rel));
            if (!full.StartsWith(Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;
            try
            {
                results.Add(File.ReadAllText(full));
                rationale.Add($"azd hook script {rel} inspected");
            }
            catch { }
        }
        return results;
    }

    private static string? ReadText(string path, int maxBytes = 20_000)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var len = (int)Math.Min(maxBytes, fs.Length);
            var buf = new byte[len];
            _ = fs.Read(buf, 0, len);
            return System.Text.Encoding.UTF8.GetString(buf);
        }
        catch { return null; }
    }

    private static IEnumerable<string> ListFiles(string dir, bool topOnly = false, params string[] specificNames)
    {
        if (!Directory.Exists(dir)) yield break;
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = !topOnly,
            IgnoreInaccessible = true
        };
        if (specificNames.Length == 0)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", opt).Take(200))
                yield return f;
        }
        else
        {
            foreach (var n in specificNames)
                foreach (var f in Directory.EnumerateFiles(dir, n, opt).Take(5))
                    yield return f;
        }
    }
}
