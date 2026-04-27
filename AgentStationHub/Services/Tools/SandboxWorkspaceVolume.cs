using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Per-deployment-session helper that backs the sandbox's <c>/workspace</c>
/// with a Docker NAMED VOLUME instead of a host bind mount. Solves the
/// "EACCES / exit 126" class of failures we saw on multi-service Node
/// samples (e.g. <c>azure-ai-travel-agents</c>) where postinstall scripts
/// for esbuild / tree-sitter / node-gyp-build produce binaries inside
/// <c>node_modules/.bin</c> that the kernel then refused to execute.
///
/// Root cause: Docker Desktop on Windows surfaces host bind mounts via
/// virtio-fs / 9p with quirks that drop the executable bit (or, on some
/// configurations, mount the path noexec). Native filesystem semantics
/// are lost. A named volume lives entirely on the Docker VM's ext4
/// filesystem � full Linux semantics, no translator in the middle.
///
/// Lifecycle:
///   * <see cref="EnsureAsync"/>: create the volume and copy the cloned
///     repo (already on disk via LibGit2Sharp) into it. Idempotent.
///   * <see cref="RemoveAsync"/>: best-effort cleanup at session end so
///     volumes don't leak across deploys. Safe to call even when the
///     volume was never created.
///   * <see cref="VolumeName"/>: deterministic name from the session id.
/// </summary>
public static class SandboxWorkspaceVolume
{
    /// <summary>Deterministic volume name for a session id.</summary>
    public static string VolumeName(string sessionId)
        => $"agentichub-work-{sessionId}";

    /// <summary>
    /// Creates the volume (if missing) and synchronises the freshly
    /// cloned <paramref name="hostWorkDir"/> into it via a one-shot
    /// alpine container. Returns the volume name on success.
    /// </summary>
    public static async Task<string> EnsureAsync(
        string sessionId,
        string hostWorkDir,
        Action<string, string> log,
        CancellationToken ct)
    {
        var volume = VolumeName(sessionId);

        // 1) Create the volume (idempotent � `docker volume create` no-ops
        //    if a volume with the same name already exists). SIGPIPE-proof
        //    via shell redirect like SandboxAzureAuth does.
        var create = await Cli.Wrap("sh")
            .WithArguments(new[] { "-c", $"docker volume create {volume} >/dev/null 2>&1" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (create.ExitCode != 0)
        {
            // Try inspect to see if it actually exists (another process
            // may have created it concurrently); if so, proceed.
            var inspect = await Cli.Wrap("sh")
                .WithArguments(new[] { "-c", $"docker volume inspect {volume} >/dev/null 2>&1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
            if (inspect.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to create or locate workspace volume '{volume}'. " +
                    "Steps that need executable binaries from node_modules will likely " +
                    "fall back to the host bind mount and fail with EACCES.");
            }
        }

        // 2) Sync the cloned files into the volume. Using `cp -a` (alpine)
        //    preserves mode bits, symlinks, ownership; we strip ANY
        //    pre-existing content first because the volume is per-session
        //    and reusing a stale one would mix repos. The `cp -a /src/. ...`
        //    form copies CONTENTS (including hidden files like `.git`)
        //    without dropping them under a sub-directory.
        log("status", $"? Staging cloned repo into per-session workspace volume ({volume})...");

        const string syncCmd =
            "set -e; " +
            "rm -rf /workspace/* /workspace/.[!.]* /workspace/..?* 2>/dev/null || true; " +
            "cp -a /src/. /workspace/; " +
            "echo 'workspace volume primed: '$(du -sh /workspace 2>/dev/null | cut -f1)";

        var syncArgs = new List<string>
        {
            "run", "--rm", "-i",
            "-v", $"{hostWorkDir}:/src:ro",
            "-v", $"{volume}:/workspace",
            "alpine:3.20",
            "sh", "-lc", syncCmd
        };

        int exit = -1;
        await foreach (var ev in Cli.Wrap("docker")
            .WithArguments(syncArgs)
            .WithValidation(CommandResultValidation.None)
            .ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o:
                    if (!string.IsNullOrWhiteSpace(o.Text)) log("info", o.Text);
                    break;
                case StandardErrorCommandEvent e:
                    if (!string.IsNullOrWhiteSpace(e.Text)) log("warn", e.Text);
                    break;
                case ExitedCommandEvent x:
                    exit = x.ExitCode;
                    break;
            }
        }

        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to populate workspace volume '{volume}' from '{hostWorkDir}' " +
                $"(exit {exit}). The deploy cannot proceed without an executable workspace.");
        }

        return volume;
    }

    /// <summary>
    /// Best-effort: remove the per-session named volume. We only do this
    /// when the session terminates (success or fatal error), so any tail-
    /// log inspection from the UI still has access to <c>s.WorkDir</c>
    /// (the host clone directory, untouched by this volume). Errors are
    /// swallowed: a leftover docker volume is annoying but not fatal.
    /// </summary>
    public static async Task RemoveAsync(string sessionId, CancellationToken ct)
    {
        var volume = VolumeName(sessionId);
        try
        {
            await Cli.Wrap("sh")
                .WithArguments(new[] { "-c", $"docker volume rm -f {volume} >/dev/null 2>&1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
        }
        catch
        {
            // Volume cleanup is opportunistic. The next deploy with the
            // same session id (extremely unlikely given GUID-based ids)
            // would re-prime the volume from scratch anyway.
        }
    }
}
