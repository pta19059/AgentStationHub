using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Owns the LONG-LIVED sandbox container for one deployment session.
///
/// ## Why this exists
///
/// Originally every plan step ran in its own ephemeral
/// <c>docker run --rm &lt;image&gt; bash -lc "..."</c>. That model meant
/// state earned by step N (Azure CLI tokens, ACR registry credentials,
/// the executable bit on <c>node_modules/.bin/*</c> after
/// <c>npm install</c>, the result of <c>export FOO=bar</c>, the
/// downloaded NuGet/npm/pip cache, etc.) was discarded before step N+1
/// started, and we then bolted on a separate fix for each class of lost
/// state: a persistent <c>~/.azure</c> volume, a persistent
/// <c>~/.docker</c> volume + post-step <c>az acr login</c> hook, a
/// per-session named volume for <c>/workspace</c>, a <c>docker-compose</c>
/// shim, an <c>env_file</c> primer, etc. Every new sample exposed a new
/// state-loss class and the loop never closed.
///
/// The architectural fix is to keep ONE container alive for the entire
/// session: <c>docker run -d --name asb-&lt;sid&gt; ... sleep infinity</c>
/// at session start, <c>docker exec</c> for each step, <c>docker rm -f</c>
/// in the session's <c>finally</c>. With that, ALL of the above becomes
/// naturally persistent for the duration of the deploy:
/// <list type="bullet">
///   <item>Azure CLI tokens (<c>~/.azure</c>) and ACR creds (<c>~/.docker</c>)
///         survive across steps without external volumes.</item>
///   <item><c>node_modules</c> and the <c>+x</c> bit set by postinstall scripts
///         persist on the volume just like before, but no longer rely on
///         it for cross-step survival.</item>
///   <item><c>export</c> in step N is visible in step N+1 because both run
///         in the same container's filesystem and (for shell-rc-like patterns)
///         in the same <c>/etc/environment</c>.</item>
///   <item>Package caches (<c>~/.npm</c>, <c>~/.cache/pip</c>, BuildKit
///         cache, the azd CLI's resolved Bicep modules) stay hot, cutting
///         repeated step times by minutes.</item>
///   <item>Doctor remediations only need ONE level of shell quoting
///         (<c>docker exec asb-&lt;sid&gt; bash -lc "..."</c>) instead of
///         the brittle <c>docker run ... bash -lc "..."</c> nesting that
///         routinely lost <c>$variable</c> substitutions.</item>
/// </list>
///
/// Per-session ACR/azure-profile volumes still mount onto the long-lived
/// container so credentials cached on a previous deploy are reused on the
/// next one � those volumes are now an ACROSS-DEPLOY optimisation, not a
/// correctness requirement for within-deploy state continuity.
///
/// Concurrency: container names embed the session id, so two parallel
/// deploys never collide. A defensive <c>docker rm -f</c> at start cleans
/// up a leftover from a previous crash with the same name.
/// </summary>
public sealed class SandboxSession : IAsyncDisposable
{
    public string ContainerName { get; }
    public string Image { get; }
    public string WorkspaceMount { get; }
    private readonly Action<string, string> _log;
    private bool _disposed;

    private SandboxSession(string containerName, string image, string workspaceMount,
        Action<string, string> log)
    {
        ContainerName = containerName;
        Image = image;
        WorkspaceMount = workspaceMount;
        _log = log;
    }

    public static async Task<SandboxSession> StartAsync(
        string sessionId,
        string image,
        string? workspaceVolume,
        string fallbackHostWorkDir,
        Action<string, string> log,
        CancellationToken ct)
    {
        // Container names: docker requires [a-zA-Z0-9_.-], we use the
        // session id which is a Guid � already safe.
        var name = "asb-" + sessionId.Replace("{", "").Replace("}", "");

        // Defensive: a previous deploy that crashed (process killed,
        // host reboot mid-run) may have left a container with this name
        // around. Force-remove BEFORE create, or `docker run` would fail
        // with "container name already in use".
        await ForceRemoveAsync(name, ct);

        var workspaceMount = workspaceVolume is not null
            ? $"{workspaceVolume}:/workspace"
            : $"{fallbackHostWorkDir}:/workspace";

        var args = new List<string>
        {
            "run", "-d", "--name", name,
            // No --rm: we manage lifecycle explicitly and want logs
            // available via `docker logs` until DisposeAsync removes
            // the container.
            "--dns", "1.1.1.1",
            "--dns", "8.8.8.8",
            // Memory / swap — sized for large templates (GPT-RAG
            // deploys ~15 ARM resources in parallel via Bicep; each
            // holds state while azd coordinates them).
            "--memory", "12g",
            "--memory-swap", "24g",
            "--memory-swappiness", "90",
            // Workspace and DooD socket. Workspace mount comes from the
            // per-session named volume (preferred) or a host bind on
            // hosts where the volume couldn't be created � see
            // SandboxWorkspaceVolume for the rationale.
            "-v", workspaceMount,
            "-v", "/var/run/docker.sock:/var/run/docker.sock",
        };

        // Azure-profile + docker-config persistent volumes. With the
        // long-lived container these are now strictly an OPTIMISATION
        // (re-use credentials across separate deploys); within a single
        // deploy `~/.azure` and `~/.docker` are already shared by all
        // exec invocations because they hit the same writable layer.
        args.AddRange(SandboxAzureAuth.VolumeMountArgs());

        // Baseline environment � set on the container so every exec
        // inherits it without having to repeat `-e` flags.
        var defaultEnv = new Dictionary<string, string>
        {
            ["AZURE_DEV_COLLECT_TELEMETRY"] = "no",
            ["AZD_DEBUG_NO_UPDATE_CHECK"] = "true",
            ["AZURE_CORE_COLLECT_TELEMETRY"] = "false",
            ["AZURE_CORE_ONLY_SHOW_ERRORS"] = "true",
            ["AZURE_CORE_NO_COLOR"] = "true",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DO_NOT_TRACK"] = "1",
            ["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no",
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["PYTHONNOUSERSITE"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
        };
        foreach (var kv in defaultEnv)
        {
            args.Add("-e");
            args.Add($"{kv.Key}={kv.Value}");
        }

        // Keep the container alive forever; we drive work via docker exec.
        args.Add(image);
        args.Add("sleep");
        args.Add("infinity");

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        int exit = -1;
        await foreach (var ev in Cli.Wrap("docker")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o: stdout.AppendLine(o.Text); break;
                case StandardErrorCommandEvent e: stderr.AppendLine(e.Text); break;
                case ExitedCommandEvent x: exit = x.ExitCode; break;
            }
        }
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to start sandbox session container '{name}' " +
                $"(exit {exit}). stderr: {stderr}");
        }

        log("info", $"Sandbox session container '{name}' started (image: {image}).");
        return new SandboxSession(name, image, workspaceMount, log);
    }

    /// <summary>
    /// Run a shell command inside the live session container.
    /// Output streams via <paramref name="onStdout"/> and
    /// <paramref name="onStderr"/>.
    /// </summary>
    public async Task<int> ExecAsync(
        string shellCommand,
        string containerCwd,
        IDictionary<string, string>? envVars,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SandboxSession));

        var args = new List<string> { "exec" };
        if (!string.IsNullOrWhiteSpace(containerCwd))
        {
            args.Add("-w");
            args.Add(containerCwd);
        }
        if (envVars is not null)
        {
            foreach (var kv in envVars)
            {
                args.Add("-e");
                args.Add($"{kv.Key}={kv.Value}");
            }
        }
        args.Add(ContainerName);
        args.Add("bash");
        args.Add("-lc");
        args.Add(shellCommand);

        int exit = -1;
        await foreach (var ev in Cli.Wrap("docker")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ListenAsync(System.Text.Encoding.UTF8, System.Text.Encoding.UTF8, ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o: onStdout(o.Text); break;
                case StandardErrorCommandEvent e: onStderr(e.Text); break;
                case ExitedCommandEvent x: exit = x.ExitCode; break;
            }
        }
        return exit;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _log("info", $"Tearing down sandbox session container '{ContainerName}'.");
            await ForceRemoveAsync(ContainerName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log("warn", $"Could not remove sandbox container '{ContainerName}' (non-fatal): {ex.Message}");
        }
    }

    private static async Task ForceRemoveAsync(string name, CancellationToken ct)
    {
        // sh wrapper to avoid any pipe-close races with Docker Desktop
        // on Windows hosts; same defensive pattern used in
        // SandboxAzureAuth.EnsureVolumeExistsAsync.
        try
        {
            await Cli.Wrap("sh")
                .WithArguments(new[] { "-c", $"docker rm -f {name} >/dev/null 2>&1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
        }
        catch
        {
            // Best effort � if docker is gone there's nothing to clean up.
        }
    }
}
