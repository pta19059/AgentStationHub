using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Manages a persistent Docker named volume that stores the sandbox's own
/// Azure CLI profile. We cannot reuse the host's MSAL token cache directly
/// because on Windows it is encrypted with DPAPI (user-specific) and is
/// undecipherable from inside a Linux container. Instead we keep a Linux-
/// native profile inside a named volume that survives across container runs.
///
/// The first deployment triggers an 'az login --use-device-code' (the code is
/// streamed to the live log). From that point on every deploy re-uses the
/// token cache stored in the volume.
/// </summary>
public static class SandboxAzureAuth
{
    public const string VolumeName = "agentichub-azure-profile";

    /// <summary>
    /// Second persistent named volume that stores the sandbox's Docker
    /// CLI config (<c>~/.docker/config.json</c>). Without this volume each
    /// step runs in a fresh ephemeral container and any <c>az acr login</c>
    /// run during step N is gone by step N+1, so a follow-up
    /// <c>azd deploy</c> / <c>docker push</c> hits the registry with no
    /// auth and fails with <c>denied: authentication required</c>. Mounting
    /// the same volume across all steps means <c>az acr login</c> only has
    /// to run once per registry per session and stays valid for every
    /// subsequent push (the token's lifetime is several hours).
    /// </summary>
    public const string DockerConfigVolumeName = "agentichub-docker-config";

    private static readonly SemaphoreSlim _sem = new(1, 1);
    private static bool _verified;

    /// <summary>
    /// Ensures the named volume exists and that the sandbox has a valid Azure
    /// login. If it is the first run (or the token expired), triggers a device
    /// code login inside the container and streams progress to the provided
    /// logger. Subsequent calls are no-ops.
    /// </summary>
    public static async Task EnsureAsync(
        string sandboxImage,
        string? tenantId,
        Action<string, string> log,
        CancellationToken ct)
    {
        await _sem.WaitAsync(ct);
        try
        {
            if (_verified) return;

            await EnsureVolumeExistsAsync(VolumeName, ct);
            // Same defensive logic for the docker-config volume so every
            // sandbox step shares one ~/.docker/config.json. Failure here
            // is non-fatal: docker push will just need an `az acr login`
            // every step (slower, but correctness-equivalent).
            await EnsureVolumeExistsAsync(DockerConfigVolumeName, ct);

            // Probe: is the volume already authenticated for our tenant?
            var probeOk = await RunInContainerAsync(
                sandboxImage, "az account show --only-show-errors",
                _ => { }, ct);
            if (probeOk)
            {
                log("info", "Sandbox Azure profile already authenticated; reusing cached credentials.");
                _verified = true;
                return;
            }

            log("status", "Sandbox has no cached Azure credentials. Starting device code login...");
            log("info", "==> Copy the code below, open the URL, and approve the sign-in.");

            var tenantArg = string.IsNullOrWhiteSpace(tenantId) ? "" : $"--tenant {tenantId} ";
            var loginCmd = $"az login --use-device-code {tenantArg}--only-show-errors";
            var loginOk = await RunInContainerAsync(
                sandboxImage, loginCmd,
                line => log("info", line), ct);

            if (!loginOk)
                throw new InvalidOperationException(
                    "Device code login failed inside the sandbox. " +
                    "Check the log above for the device code URL and retry.");

            // Set the default subscription / location hints if we have a tenant.
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                await RunInContainerAsync(
                    sandboxImage,
                    $"az account set --subscription $(az account list --query \"[?tenantId=='{tenantId}'] | [0].id\" -o tsv)",
                    _ => { }, ct);
            }

            log("info", "Sandbox Azure login completed. Credentials will be reused on next deploys.");
            _verified = true;
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>
    /// Docker CLI args that mount the named volume as /root/.azure so both
    /// subsequent steps of the same deploy AND future deploys see the same
    /// authenticated profile.
    /// </summary>
    public static IEnumerable<string> VolumeMountArgs()
    {
        yield return "-v";
        yield return $"{VolumeName}:/root/.azure";
        yield return "-e";
        yield return "AZURE_CONFIG_DIR=/root/.azure";
        // Persistent docker CLI config: see DockerConfigVolumeName XML doc.
        // DOCKER_CONFIG points the docker CLI at our volume (default would
        // be the user's $HOME/.docker which is fine here too, but being
        // explicit prevents surprises if a step changes HOME).
        yield return "-v";
        yield return $"{DockerConfigVolumeName}:/root/.docker";
        yield return "-e";
        yield return "DOCKER_CONFIG=/root/.docker";
    }

    private static async Task EnsureVolumeExistsAsync(string volumeName, CancellationToken ct)
    {
        var VolumeName = volumeName; // keep the rest of the method readable
        // Why this is so defensive: on Docker Desktop (Windows / macOS)
        // the host daemon socket is a Unix socket FORWARDED over a Windows
        // named pipe or a lightweight VM proxy. Short-lived commands like
        // 'docker volume create' that write a trailing newline to stdout
        // can race with the forwarded pipe's close sequence and get
        // SIGPIPE (exit 141), even when the .NET side sets an explicit
        // PipeTarget. We saw this reliably on arm64 Docker Desktop.
        //
        // The reliable workaround is to have a REAL shell redirect stdout
        // and stderr to /dev/null before docker ever writes: with
        //   sh -c 'docker volume create X >/dev/null 2>&1'
        // docker's stdout is connected to the /dev/null device file (not
        // a pipe we own), so there is nothing that can be closed out from
        // under it.
        //
        // As a final safety net, if volume creation still fails we log a
        // warning and return WITHOUT throwing: the volume is an
        // optimisation (caches the sandbox's 'az login' across deploys),
        // not a correctness requirement. Without it the first sandbox
        // step will ask for a device code once per deploy instead of
        // reusing a cached token. Annoying but recoverable � preferable
        // to aborting the whole pipeline.

        // Step 1: already present? (cheap, cached after first run)
        var inspect = await Cli.Wrap("sh")
            .WithArguments(new[] { "-c", $"docker volume inspect {VolumeName} >/dev/null 2>&1" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (inspect.ExitCode == 0) return;

        // Step 2: create, SIGPIPE-proof via shell redirect.
        async Task<int> TryCreate()
        {
            var r = await Cli.Wrap("sh")
                .WithArguments(new[] { "-c", $"docker volume create {VolumeName} >/dev/null 2>&1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
            return r.ExitCode;
        }

        var exit = await TryCreate();
        if (exit != 0)
        {
            await Task.Delay(500, ct);
            exit = await TryCreate();
        }

        if (exit == 0) return;

        // Maybe another process created it concurrently � verify.
        var reinspect = await Cli.Wrap("sh")
            .WithArguments(new[] { "-c", $"docker volume inspect {VolumeName} >/dev/null 2>&1" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (reinspect.ExitCode == 0) return;

        // Graceful degradation: log + continue. The sandbox deploy will
        // still work, it just won't cache Azure credentials between runs.
        // The caller bubbles logs up to the UI via the onLine callback
        // passed to EnsureAsync; we cannot access it here, so we write
        // to Console � the containerised app captures it in docker logs.
        Console.Error.WriteLine(
            $"[SandboxAzureAuth] WARN: could not create docker volume '{VolumeName}' " +
            $"(exit {exit}). Continuing without cached Azure auth � each deploy " +
            "may require a fresh device-code login. " +
            "If this persists, restart Docker Desktop and try again.");
    }

    private static async Task<bool> RunInContainerAsync(
        string image, string shellCommand, Action<string> onLine, CancellationToken ct)
    {
        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--dns", "1.1.1.1",
            "--dns", "8.8.8.8",
            "--memory", "4g",
            "--memory-swap", "6g"
        };
        args.AddRange(VolumeMountArgs());
        args.Add(image);
        args.Add("bash");
        args.Add("-lc");
        args.Add(shellCommand);

        int exit = -1;
        await foreach (var ev in Cli.Wrap("docker")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o: onLine(o.Text); break;
                case StandardErrorCommandEvent e:  onLine(e.Text); break;
                case ExitedCommandEvent x:         exit = x.ExitCode; break;
            }
        }
        return exit == 0;
    }
}
