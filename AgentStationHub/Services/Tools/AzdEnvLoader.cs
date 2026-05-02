using System.Text;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Reads <c>azd env get-values</c> from the workspace root after every
/// azd-touching step and surfaces the result as a dictionary the
/// orchestrator can merge into the environment of subsequent steps.
///
/// ## Why
/// The Doctor (and the Strategist) frequently emit recovery commands
/// that need azd-derived values like
/// <c>AZURE_CONTAINER_REGISTRY_NAME</c>, <c>AZURE_RESOURCE_GROUP</c> or
/// <c>SERVICE_*_IMAGE_NAME</c>. Until now they tried to obtain them via
/// inline shell substitution:
/// <code>
/// REGISTRY_NAME=$(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | cut -d= -f2 | tr -d '"')
/// </code>
/// That pattern is fragile in three ways: (1) <c>azd env get-values</c>
/// only works from the azd project root (typically <c>/workspace</c>),
/// while remediation steps usually <c>cd</c> into a service subdirectory
/// for the <c>az acr build</c> context � the substitution silently
/// returns an empty string and the next argument (<c>--registry</c>)
/// receives nothing, producing the cryptic
/// <c>argument --registry/-r: expected one argument</c> error; (2) any
/// quoting mismatch in the LLM-authored shell snippet (we've seen
/// <c>tr -d '\"'</c> turn into <c>tr -d ''</c> after an extra escape pass)
/// strips real quote characters; (3) the same lookup is repeated in
/// every step instead of done once, multiplying the failure surface.
///
/// This loader lifts the lookup OUT of the shell. Once after every
/// azd-touching step the orchestrator calls
/// <see cref="LoadAndMergeAsync"/>, which runs the get-values from
/// <c>/workspace</c> exactly once and adds the resulting key/value pairs
/// to the per-step environment dictionary that <see cref="DockerShellTool"/>
/// passes via <c>-e</c> to the next exec. From the recovery step's
/// point of view <c>$AZURE_CONTAINER_REGISTRY_NAME</c> is just a
/// regular environment variable � no <c>azd env get-values | grep</c>
/// pipeline, no <c>cd /workspace</c> dance, no quoting hazard.
///
/// Best-effort: if azd has no env yet (first step is still creating it),
/// or the load returns non-zero, we log at info level and leave
/// <c>env</c> untouched. The next step proceeds with whatever env it
/// already had.
/// </summary>
public static class AzdEnvLoader
{
    /// <summary>
    /// Runs <c>azd env get-values</c> in <c>/workspace</c> via the live
    /// session container and merges the dotenv-formatted output into
    /// <paramref name="env"/>. Returns the number of keys merged
    /// (0 on best-effort failure).
    /// </summary>
    public static async Task<int> LoadAndMergeAsync(
        DockerShellTool docker,
        IDictionary<string, string> env,
        Action<string, string> log,
        CancellationToken ct)
    {
        var capture = new StringBuilder();
        // We deliberately bypass DockerShellTool.RunAsync's stdout pipe
        // so the captured output isn't streamed to the Live log (it
        // would be noisy and contain redacted-looking secrets). Instead
        // we run a single exec that writes the dotenv to a tmp file and
        // a final marker, then we read the tmp file via cat with a
        // distinctive sentinel so we can pull only that section out of
        // the streamed log. Simpler approach: use a short bash script
        // that emits ONLY the dotenv between two known markers.
        const string marker = "__AZD_ENV_LOADER__";
        var script =
            "set +e; cd /workspace || exit 0; " +
            $"echo '{marker}-BEGIN'; " +
            "azd env get-values 2>/dev/null; " +
            $"echo '{marker}-END'";

        // Reuse the silence budget at a small value: this helper should
        // never take more than a couple of seconds; if it does, azd is
        // ill, the caller's next step will surface the real problem.
        DockerShellResult result;
        try
        {
            // Pipe the streaming log into our local buffer instead of
            // the orchestrator's Log callback. We achieve this by
            // wrapping DockerShellTool calls behind a short-circuit:
            // since the public API streams via _onLog, we accept that
            // and rely on the marker-based extraction afterwards.
            // (Pragmatic compromise: a few extra info lines in the live
            // log; the orchestrator filters them out at the UI layer.)
            // Silence budget must comfortably exceed the cold-start cost of
            // the `az account get-access-token` prewarm shim that
            // DockerShellTool injects when the script mentions `azd `.
            // That shim writes ONLY to /dev/null; on a freshly-started
            // sandbox container Python+azure-cli+MSAL fetch can sit
            // genuinely silent for 10-20s while the watcher would
            // mis-classify it as a hang and kill the loader at "0 min"
            // (see https://github.com/pta19059/AgentStationHub commit
            // history). 60s is generous enough to absorb any plausible
            // cold-start while still bounding a real wedge.
            result = await docker.RunAsync(
                script,
                ".",
                envVars: null,
                timeout: TimeSpan.FromSeconds(90),
                ct: ct,
                silenceBudget: TimeSpan.FromSeconds(60),
                // Big enough to retain ALL of `azd env get-values`
                // output (typical templates emit 30-50 keys) plus the
                // BEGIN/END markers, even when azd interleaves its
                // own progress/log lines that would otherwise evict
                // env entries from the default 40-line ring buffer.
                tailSize: 400);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log("info", $"azd env loader: skipped ({ex.GetType().Name}: {ex.Message}).");
            return 0;
        }

        if (result.ExitCode != 0)
        {
            // Most common case: there's no azd environment yet because
            // the very first azd-related step was something like
            // `azd env new <name>` and it hasn't completed its first
            // run. Not interesting � just keep going.
            return 0;
        }

        // The result.TailLog only retains the last ~40 lines so it may
        // not contain the full env. Re-run with a more targeted command
        // that filters to just the keys we care about. For now we parse
        // the tail; for typical samples the env has < 40 keys so this
        // works in practice. If a sample exceeds 40 keys we will see a
        // partial merge; a follow-up step will fill in the rest.
        var added = ParseDotenvSection(result.TailLog, marker, env);
        if (added > 0)
        {
            log("info",
                $"azd env loader: merged {added} value(s) into the next step's environment.");
        }

        // Derive aliases that azd does NOT export but the Doctor /
        // Strategist routinely look up. Templates frequently expose
        // AZURE_CONTAINER_REGISTRY_ENDPOINT (e.g. "myreg.azurecr.io")
        // but never AZURE_CONTAINER_REGISTRY_NAME — yet az acr build
        // requires the bare name, and every LLM-emitted recovery uses
        // the NAME variable. Same story for AZURE_RESOURCE_GROUP: it
        // is encoded inside every AZURE_RESOURCE_*_ID but not exposed
        // as a stand-alone variable.
        // Without these aliases, AzdEnvSubstitutor cannot find the key
        // in env and conservatively leaves the inline lookup intact,
        // which then resolves to the empty string in the shell and
        // burns the Doctor budget on shape-of-pipeline variations of
        // a fundamentally unrecoverable extraction. Filling them here
        // closes the loop deterministically.
        var derived = DeriveCommonAliases(env, log);
        return added + derived;
    }

    /// <summary>
    /// Adds well-known aliases the Doctor expects but azd never
    /// exports. Idempotent: never overwrites an existing key.
    /// </summary>
    private static int DeriveCommonAliases(
        IDictionary<string, string> env, Action<string, string> log)
    {
        int n = 0;

        // AZURE_CONTAINER_REGISTRY_NAME ← strip ".azurecr.io" suffix
        // from AZURE_CONTAINER_REGISTRY_ENDPOINT.
        if (!env.ContainsKey("AZURE_CONTAINER_REGISTRY_NAME") &&
            env.TryGetValue("AZURE_CONTAINER_REGISTRY_ENDPOINT", out var endpoint) &&
            !string.IsNullOrWhiteSpace(endpoint))
        {
            var name = endpoint.Trim();
            const string suffix = ".azurecr.io";
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
            // Strip a possible scheme just in case.
            var schemeIdx = name.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) name = name[(schemeIdx + 3)..];
            if (!string.IsNullOrWhiteSpace(name))
            {
                env["AZURE_CONTAINER_REGISTRY_NAME"] = name;
                log("info",
                    $"azd env loader: derived AZURE_CONTAINER_REGISTRY_NAME='{name}' " +
                    "from AZURE_CONTAINER_REGISTRY_ENDPOINT.");
                n++;
            }
        }

        // AZURE_RESOURCE_GROUP ← parse from any AZURE_RESOURCE_*_ID
        // (full ARM resource ID: /subscriptions/<sub>/resourceGroups/<rg>/...).
        if (!env.ContainsKey("AZURE_RESOURCE_GROUP"))
        {
            foreach (var kv in env)
            {
                if (!kv.Key.StartsWith("AZURE_RESOURCE_", StringComparison.Ordinal) ||
                    !kv.Key.EndsWith("_ID", StringComparison.Ordinal)) continue;
                var rg = ExtractResourceGroup(kv.Value);
                if (!string.IsNullOrWhiteSpace(rg))
                {
                    env["AZURE_RESOURCE_GROUP"] = rg;
                    log("info",
                        $"azd env loader: derived AZURE_RESOURCE_GROUP='{rg}' from {kv.Key}.");
                    n++;
                    break;
                }
            }
        }

        return n;
    }

    private static string? ExtractResourceGroup(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        const string token = "/resourceGroups/";
        var i = resourceId.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + token.Length;
        var end = resourceId.IndexOf('/', start);
        return end < 0 ? resourceId[start..] : resourceId[start..end];
    }

    private static int ParseDotenvSection(
        string tail, string marker, IDictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(tail)) return 0;

        var beginIdx = tail.LastIndexOf(marker + "-BEGIN", StringComparison.Ordinal);
        var endIdx = tail.LastIndexOf(marker + "-END", StringComparison.Ordinal);
        if (beginIdx < 0 || endIdx < 0 || endIdx <= beginIdx) return 0;

        var section = tail.Substring(
            beginIdx + (marker + "-BEGIN").Length,
            endIdx - beginIdx - (marker + "-BEGIN").Length);

        int added = 0;
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Strip surrounding double quotes (azd env get-values
            // emits values quoted by default).
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            // Replace the value if it differs � azd env values are the
            // ground truth, so they win over anything the planner
            // may have stamped earlier from stale plan defaults.
            env[key] = value;
            added++;
        }
        return added;
    }
}
