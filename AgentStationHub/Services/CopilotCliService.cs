using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace AgentStationHub.Services;

/// <summary>
/// Manages the long-lived sidecar container that hosts GitHub Copilot CLI.
/// The image is built (if missing) and the container is started once at
/// application boot, so the user-visible "Open Copilot CLI" button just
/// has to <c>docker exec</c> into a warm process — no provisioning lag.
///
/// Lifecycle is intentionally fire-and-forget on startup: a failure here
/// must not block the rest of the app (the deploy orchestrator does not
/// depend on Copilot). The hub surface degrades gracefully when the
/// container is missing.
/// </summary>
public sealed class CopilotCliService : IHostedService
{
    public const string ImageName     = "agentichub/copilot-cli:latest";
    public const string ContainerName = "agentichub-copilot-cli";
    public const string HomeVolume    = "agentichub-copilot-home";

    /// <summary>
    /// Host port that ttyd inside the sidecar is published on. Bound to
    /// 127.0.0.1 only so the terminal is reachable from the local browser
    /// (which talks to the iframe directly) but not from the network.
    /// Kept in sync with the EXPOSE in
    /// <c>Dockerfiles/copilot-cli.Dockerfile</c>.
    /// </summary>
    public const int HostPort = 7681;

    private readonly ILogger<CopilotCliService> _log;
    private readonly IHostEnvironment _env;

    // Where the image's Dockerfile lives. We resolve it at runtime so the
    // service works both in the published Docker image (ContentRoot=/app)
    // and on a bare-metal dev launch (ContentRoot=AgentStationHub/).
    private string DockerfilePath => ResolveDockerfilePath();

    public CopilotCliService(ILogger<CopilotCliService> log, IHostEnvironment env)
    {
        _log = log;
        _env = env;
    }

    /// <summary>
    /// True when both the image and a running container were detected
    /// (or successfully created) at the last <see cref="EnsureReadyAsync"/>
    /// call. The hub uses this to short-circuit attach attempts with a
    /// useful error rather than spawning a doomed <c>docker exec</c>.
    /// </summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Last error message captured during a failed bring-up. Surfaced
    /// to the UI so the operator does not have to dig in container logs.
    /// </summary>
    public string? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run on a background thread so app startup is not gated by an
        // image build (can take a couple of minutes on first run).
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.LogWarning(ex,
                    "CopilotCliService background bring-up failed. " +
                    "The 'Open Copilot CLI' button will surface the error " +
                    "until the next manual attempt.");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Idempotent: builds the image if missing, creates and starts the
    /// container if missing, restarts it if stopped. Safe to call from
    /// the hub on demand (e.g. when the user clicks Reconnect after a
    /// host docker daemon restart).
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (!await DockerAvailableAsync(ct))
        {
            LastError =
                "Docker CLI not found on the host running AgentStationHub. " +
                "Install Docker Desktop / engine and restart the app.";
            IsReady = false;
            return;
        }

        if (!await ImageExistsAsync(ct))
        {
            _log.LogInformation("Building Copilot CLI image '{Image}' …", ImageName);
            await BuildImageAsync(ct);
        }

        var status = await GetContainerStatusAsync(ct);
        if (status != "missing" && await ContainerNeedsRecreateAsync(ct))
        {
            // Two reasons to recreate in-place:
            //   1. Pre-ttyd containers were created without -p 7681 / -b
            //      /copilot and we can't mutate that on a live container.
            //   2. We just switched hosting mode (bare-metal <-> in-container)
            //      and the publish-vs-network-attach shape mismatches.
            // The named volume keeps Copilot auth + history across the swap.
            _log.LogInformation(
                "Copilot CLI container '{Container}' has stale port/network " +
                "shape for the current hosting mode — removing and recreating.",
                ContainerName);
            await RemoveContainerAsync(ct);
            status = "missing";
        }
        switch (status)
        {
            case "running":
                break;
            case "missing":
                _log.LogInformation("Creating Copilot CLI container '{Container}'.", ContainerName);
                await RunContainerAsync(ct);
                break;
            default: // exited / created / paused
                _log.LogInformation(
                    "Copilot CLI container is in state '{Status}', restarting.", status);
                await StartContainerAsync(ct);
                break;
        }

        IsReady   = true;
        LastError = null;
        _log.LogInformation("Copilot CLI sidecar ready ({Container}).", ContainerName);
    }

    // ------------------------------------------------------------------ docker probes

    private static async Task<bool> DockerAvailableAsync(CancellationToken ct)
    {
        try
        {
            var r = await Cli.Wrap("docker")
                .WithArguments("version --format {{.Client.Version}}")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> ImageExistsAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("docker")
            .WithArguments($"image inspect {ImageName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return r.ExitCode == 0;
    }

    /// <summary>
    /// Returns one of: "running", "exited", "created", "paused", "missing".
    /// </summary>
    private static async Task<string> GetContainerStatusAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("docker")
            .WithArguments($"inspect -f {{{{.State.Status}}}} {ContainerName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (r.ExitCode != 0) return "missing";
        return r.StandardOutput.Trim();
    }

    /// <summary>
    /// True when the existing container's shape doesn't match the
    /// current hosting mode and must be removed/recreated:
    ///
    ///   - Bare-metal mode wants a host-loopback publish on 7681 (the
    ///     iframe talks directly to localhost). If the container was
    ///     created without that publish, recreate.
    ///   - In-container mode (Hub running inside Docker on the VM)
    ///     wants NO host publish; instead the container is attached to
    ///     the Hub's docker network and reached as
    ///     'agentichub-copilot-cli:7681' via the Hub's reverse proxy.
    ///     If a stale container still has a host publish, recreate.
    /// </summary>
    private static async Task<bool> ContainerNeedsRecreateAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("docker")
            .WithArguments($"inspect -f {{{{json .HostConfig.PortBindings}}}} {ContainerName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (r.ExitCode != 0) return false; // can't inspect -> let normal flow handle
        var hasHostPublish = r.StandardOutput.Contains("\"7681/tcp\"");
        var inContainer = IsRunningInContainer();
        // Bare-metal: must have publish. In-container: must NOT have publish.
        return inContainer ? hasHostPublish : !hasHostPublish;
    }

    /// <summary>
    /// True when the .NET host is running inside a container (the Hub's
    /// own Dockerfile sets DOTNET_RUNNING_IN_CONTAINER=true; /.dockerenv
    /// is the kernel-level breadcrumb every Docker runtime drops). When
    /// true, we must NOT publish the sidecar port on the host (it'd
    /// race the Hub container's loopback) and must attach the sidecar
    /// to the Hub's compose network so it's reachable as
    /// agentichub-copilot-cli:7681 from the Hub process.
    /// </summary>
    public static bool IsRunningInContainer() =>
        File.Exists("/.dockerenv") ||
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true", StringComparison.OrdinalIgnoreCase);

    private static async Task RemoveContainerAsync(CancellationToken ct)
    {
        await Cli.Wrap("docker")
            .WithArguments($"rm -f {ContainerName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
    }

    // ------------------------------------------------------------------ docker actions

    private async Task BuildImageAsync(CancellationToken ct)
    {
        var df = DockerfilePath;
        if (!File.Exists(df))
            throw new FileNotFoundException(
                $"Copilot CLI Dockerfile not found at '{df}'. " +
                "Verify the build copied Dockerfiles/ into the runtime image.");

        // Streaming build context via stdin keeps the operation independent
        // of the host filesystem layout (works the same way inside the
        // app container and on a dev box).
        var sb = new StringBuilder();
        await Cli.Wrap("docker")
            .WithArguments($"build -t {ImageName} -f - .")
            .WithStandardInputPipe(PipeSource.FromFile(df))
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sb))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        // We re-check the image instead of relying on exit code so a flaky
        // BuildKit emitting a non-zero on a retryable warning still counts
        // as success when the image actually exists.
        if (!await ImageExistsAsync(ct))
            throw new InvalidOperationException(
                $"docker build failed for {ImageName}. Last 2KB of output:\n" +
                Tail(sb.ToString(), 2048));
    }

    private async Task RunContainerAsync(CancellationToken ct)
    {
        var inContainer = IsRunningInContainer();
        // Bare-metal dev box: publish ttyd to host loopback so the local
        // browser hits http://localhost:7681 directly through the Hub's
        // /copilot/ reverse proxy (which targets localhost:7681 in that
        // mode). 127.0.0.1 keeps it off external interfaces.
        // VM/in-container: skip the publish entirely. The Hub process
        // and the sidecar share the compose docker network, and the
        // Hub's reverse proxy targets http://agentichub-copilot-cli:7681
        // directly. Publishing on the VM host would force operators to
        // open another NSG hole, which we explicitly do NOT want.
        var portFlag = inContainer ? "" : $"-p 127.0.0.1:{HostPort}:7681 ";
        // -d                       : detached, lifecycle managed by daemon.
        // --restart unless-stopped : survives daemon restarts but obeys an
        //                            explicit `docker stop`.
        // -v <vol>:/root           : persists Copilot/gh auth and history.
        // --name                   : stable handle for `docker exec` & inspect.
        var args =
            $"run -d --name {ContainerName} " +
            $"--restart unless-stopped " +
            portFlag +
            $"-v {HomeVolume}:/root " +
            $"-e TERM=xterm-256color " +
            $"{ImageName}";
        var r = await Cli.Wrap("docker")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                "docker run failed for Copilot CLI sidecar:\n" + r.StandardError);

        if (inContainer)
        {
            await AttachToHubNetworkAsync(ct);
        }
    }

    /// <summary>
    /// In compose-on-VM mode the Hub container ('agentichub-app') is
    /// attached to the project's default docker network (typically
    /// '<dir>_default' where <dir> is the directory name of the compose
    /// project). The newly-created Copilot sidecar is on the bridge
    /// network only, so the Hub can't resolve 'agentichub-copilot-cli'.
    /// Discover the Hub's first non-bridge network and connect the
    /// sidecar to it. Idempotent: a 'network connect' against an
    /// already-attached container errors out and we just log it.
    /// </summary>
    private async Task AttachToHubNetworkAsync(CancellationToken ct)
    {
        // Read the Hub container's networks. The Go template emits one
        // network name per line; we pick the first non-'bridge' one.
        var inspect = await Cli.Wrap("docker")
            .WithArguments(new[]
            {
                "inspect", "-f",
                "{{range $k,$_ := .NetworkSettings.Networks}}{{$k}}\n{{end}}",
                "agentichub-app"
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (inspect.ExitCode != 0)
        {
            _log.LogWarning(
                "Could not inspect Hub container 'agentichub-app' to find " +
                "its docker network. Copilot CLI panel may be unreachable. " +
                "stderr: {Err}", inspect.StandardError);
            return;
        }
        var net = inspect.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(n => !string.Equals(n, "bridge", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(net))
        {
            _log.LogWarning(
                "Hub container has no non-bridge network; Copilot sidecar " +
                "will not be reachable from the Hub process.");
            return;
        }
        var connect = await Cli.Wrap("docker")
            .WithArguments($"network connect {net} {ContainerName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (connect.ExitCode == 0)
        {
            _log.LogInformation(
                "Attached Copilot sidecar to Hub network '{Net}'.", net);
        }
        else
        {
            // Already attached is fine; log at info to avoid noise.
            _log.LogInformation(
                "docker network connect returned non-zero (likely already " +
                "attached): {Err}", connect.StandardError.Trim());
        }
    }

    private static async Task StartContainerAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("docker")
            .WithArguments($"start {ContainerName}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                "docker start failed for Copilot CLI sidecar:\n" + r.StandardError);
    }

    // ------------------------------------------------------------------ helpers

    private string ResolveDockerfilePath()
    {
        // 1. Runtime image: Dockerfiles/ is copied to /app/Dockerfiles
        //    (added in the main Dockerfile alongside published bits).
        // 2. Bare-metal dev: ContentRoot is the AgentStationHub project,
        //    so the file is one level up at ../Dockerfiles/.
        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, "Dockerfiles", "copilot-cli.Dockerfile"),
            Path.Combine(_env.ContentRootPath, "..", "Dockerfiles", "copilot-cli.Dockerfile"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return candidates[0];
    }

    private static string Tail(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[^max..];
}
