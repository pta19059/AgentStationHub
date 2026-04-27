using System.Text.RegularExpressions;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Pre-execution rewriter that turns brittle inline shell pipelines like
/// <code>
///   REG=$(azd env get-values | sed -n 's/^AZURE_CONTAINER_REGISTRY_NAME=//p' | tr -d '"')
///   RG=$(azd env get-values | grep AZURE_RESOURCE_GROUP | cut -d= -f2 | tr -d '\"')
///   `azd env get-values | awk -F= '/^AZURE_TENANT_ID/{print $2}' | tr -d '\"'`
/// </code>
/// into direct references to environment variables the orchestrator has
/// already injected into the step (via <see cref="AzdEnvLoader"/>).
///
/// ## Why
/// After every azd-touching step the orchestrator runs
/// <c>azd env get-values</c> once at <c>/workspace</c> (the project root,
/// the ONLY cwd where the command works) and merges the dotenv into the
/// next step's <c>env</c> dictionary, which <see cref="DockerShellTool"/>
/// passes via <c>docker exec -e</c>. So variables like
/// <c>AZURE_CONTAINER_REGISTRY_NAME</c> are already available as ordinary
/// shell variables in every subsequent step.
///
/// In practice the Strategist and especially the Doctor keep emitting
/// commands that re-query azd inline:
/// <list type="bullet">
///   <item>they <c>cd</c> into a service subdir for the build context;</item>
///   <item>then run <c>$(azd env get-values | grep X | ...)</c> from that
///         subdir;</item>
///   <item><c>azd env get-values</c> from a subdir often returns empty
///         (there's no .azure folder there, AZURE_ENV_NAME isn't set, the
///         project root walk-up fails, etc.) so the substitution becomes
///         the empty string;</item>
///   <item>the next argument (<c>--registry </c>) sees nothing, az fails
///         with <c>argument --registry/-r: expected one argument</c>;</item>
///   <item>the Doctor tries again with the same pattern and a different
///         pipeline shape (<c>sed</c> ? <c>grep|cut</c> ? <c>awk</c>) and
///         exhausts its 8-attempt budget without ever just using
///         <c>$AZURE_CONTAINER_REGISTRY_NAME</c>.</item>
/// </list>
/// We saw this happen verbatim across multiple deploys (<c>azure-ai-
/// travel-agents</c>); it's a pure preprocessing failure that has nothing
/// to do with the actual deploy.
///
/// This rewriter sidesteps the LLM's choice of shell pipeline. For every
/// command the orchestrator is about to execute, we scan for
/// <c>$(...)</c> and backtick-quoted command substitutions whose body
/// contains <c>azd env get-values</c>, extract the single
/// <c>AZURE_*</c>/<c>SERVICE_*</c>/etc. variable being filtered out, and
/// rewrite the whole substitution into a plain bash variable reference
/// <c>"$AZURE_CONTAINER_REGISTRY_NAME"</c> when we already have that
/// variable in <c>env</c>. The resulting command is functionally
/// identical when the env is correctly populated and far more robust:
/// no cwd dependency, no pipe quoting hazards, no LLM creativity.
///
/// Best-effort and conservative: if we cannot identify the variable
/// being filtered (e.g. complex regex, multiple keys in one pipeline),
/// or the variable isn't in <c>env</c>, the substitution is left
/// untouched � the original behaviour is preserved.
/// </summary>
public static class AzdEnvSubstitutor
{
    // Match `$(...)` and backtick-quoted command substitutions. Both are
    // allowed to span multiple lines because the Doctor sometimes emits
    // multi-line pipelines.
    private static readonly Regex DollarParen =
        new(@"\$\(([^()]*)\)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Backtick =
        new(@"`([^`]*)`", RegexOptions.Compiled | RegexOptions.Singleline);

    // Find the first AZD-style env var name a pipeline is filtering on.
    // Covers every shape we've seen the Doctor emit:
    //   sed -n 's/^AZURE_X=//p'
    //   sed -n '/AZURE_X/s/^AZURE_X=//p'
    //   grep '^AZURE_X='     /     grep AZURE_X
    //   awk -F= '/^AZURE_X/{print $2}'
    //   awk '/AZURE_X/{...}'
    // The captured name must look like an env var: uppercase letters,
    // digits and underscores, starting with a letter. We restrict the
    // first segment to AZURE_ / SERVICE_ / AZD_ / APP_ / CONTAINER_
    // to avoid accidentally rewriting unrelated variable lookups.
    private static readonly Regex KeyName =
        new(@"\b(AZURE_[A-Z0-9_]+|SERVICE_[A-Z0-9_]+|AZD_[A-Z0-9_]+|APP_[A-Z0-9_]+|CONTAINER_[A-Z0-9_]+)\b",
            RegexOptions.Compiled);

    /// <summary>
    /// Returns a (possibly identical) shell command with eligible
    /// <c>$(azd env get-values | ...)</c> substitutions replaced by
    /// <c>"$KEY"</c>. <paramref name="onRewrite"/> is called once per
    /// substitution so the orchestrator can log a one-line audit trail
    /// to the live log.
    /// </summary>
    public static string Rewrite(
        string command,
        IReadOnlyDictionary<string, string> env,
        Action<string>? onRewrite = null)
    {
        if (string.IsNullOrEmpty(command) || env.Count == 0) return command;

        // Snapshot keys we know � case-sensitive, like POSIX.
        // No allocation of a HashSet for tiny dicts; ContainsKey on
        // a Dictionary<,> is already O(1).

        string Replace(Match m, char openQuote, char closeQuote)
        {
            var inner = m.Groups[1].Value;
            if (inner.IndexOf("azd env get-values", StringComparison.Ordinal) < 0)
                return m.Value; // not ours; leave alone.

            // Only one azd lookup per substitution: trying to do two in
            // the same $(...) is unusual and likely intentional, leave
            // it alone.
            var keys = KeyName.Matches(inner);
            if (keys.Count == 0) return m.Value;

            // Many of the LLM-emitted pipelines repeat the same name
            // multiple times (`grep ^AZURE_X` and then `sed s/^AZURE_X=`
            // in the same pipeline). Pick the most-frequent name and
            // require all matches to either be that name or one of the
            // common pipeline-internal tokens we should ignore.
            var name = keys
                .GroupBy(km => km.Value)
                .OrderByDescending(g => g.Count())
                .First().Key;

            if (!env.TryGetValue(name, out var value))
                return m.Value; // we don't know the value; leave alone.

            // CRITICAL: emit the LITERAL value, unquoted, when it is
            // safe to do so. Why not `"<value>"` (double-quoted)?
            // Because the LLM frequently wraps the entire step in
            // `bash -lc "..."`, so the `$(...)` we are rewriting often
            // lives INSIDE an outer double-quoted shell argument.
            // Emitting `"value"` would close that outer double-quote
            // prematurely, splitting the argument and producing
            // invalid CLI invocations (e.g. `--registry ""` or
            // `--registry "frag"ment`, surfacing as the cryptic
            // "Registry names may contain only alpha numeric
            // characters" error). And why not `$AZURE_X` (a bare
            // env-var reference)? Because the inner shell launched
            // by `bash -lc` does NOT always inherit our `docker exec
            // -e` environment in the way you'd expect: a login bash
            // sources `/etc/profile` and friends, which on the Mariner
            // image used by the sandbox can shadow or unset variables
            // with names like `AZURE_*`. We see the empty-expansion
            // failure mode in practice: `ACR_NAME=$AZURE_CONTAINER_REGISTRY_NAME`
            // assigns the empty string and the next `--name $ACR_NAME`
            // breaks with `argument --name/-n: expected one argument`.
            //
            // The literal-unquoted path side-steps both hazards. It
            // is safe IFF the value is composed of characters that
            // bash treats as a single token (no whitespace, no shell
            // metachars). Every realistic azd value qualifies — ACR
            // names, ARM IDs, image tags, URLs, environment names —
            // and the IsSafeBareValue allowlist enforces it. On the
            // rare case a value contains an unsafe character, we
            // leave the original `$(...)` substitution intact so the
            // surrounding pipeline (already in the user's command)
            // can quote it.
            if (IsSafeBareValue(value))
            {
                onRewrite?.Invoke(
                    $"azd-env substitution: replaced inline `azd env get-values` lookup of " +
                    $"{name} with the literal value '{value}'.");
                return value;
            }

            // Fallback: value contains characters that are unsafe to
            // splat unquoted. Leave the original substitution intact;
            // the pre-existing pipeline (which DOES quote the output)
            // handles it.
            return m.Value;
        }

        var rewritten = DollarParen.Replace(command, m => Replace(m, '$', ')'));
        rewritten   = Backtick   .Replace(rewritten, m => Replace(m, '`', '`'));
        return rewritten;
    }

    /// <summary>
    /// True when the value is composed exclusively of characters that
    /// are safe inside a bare `$NAME` shell expansion (no whitespace,
    /// no quotes, no shell metachars). Covers every realistic azd
    /// value we have seen across templates: ARM resource IDs, ACR
    /// names, image tags, URLs, environment names. Conservative on
    /// purpose — anything off this allowlist falls back to the
    /// original substitution.
    /// </summary>
    private static bool IsSafeBareValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            var ok = char.IsLetterOrDigit(c)
                  || c == '-' || c == '_' || c == '.'
                  || c == '/' || c == ':';
            if (!ok) return false;
        }
        return true;
    }
}
