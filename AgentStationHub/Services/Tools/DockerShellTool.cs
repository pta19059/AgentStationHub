using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

public sealed record DockerShellResult(int ExitCode, string TailLog)
{
    /// <summary>
    /// When true the step was aborted because it went silent for longer
    /// than the configured silence budget � no stdout/stderr activity for
    /// N minutes while still "running". This distinguishes a genuine hang
    /// (docker buildx stuck on npm install inside an Angular Dockerfile,
    /// azd deploy waiting on a revision that will never become healthy)
    /// from a step that takes a long time but is producing output.
    /// The orchestrator treats this differently from exit-code failures:
    /// the Doctor gets a dedicated 'BuildHang' / 'StepSilent' signature
    /// and can propose a switch strategy (e.g. remote ACR build).
    /// </summary>
    public bool TimedOutBySilence { get; init; }
}

/// <summary>
/// Per-step shell driver. Wraps a long-lived <see cref="SandboxSession"/>
/// and turns each plan step into a single <c>docker exec</c> against it,
/// preserving filesystem, environment, and credential state across steps
/// for free � see SandboxSession for the architectural rationale.
///
/// This class is intentionally thin: the heavy lifting (container
/// creation, mounts, lifecycle) lives on the session. Responsibilities
/// retained here are:
/// <list type="bullet">
///   <item>Streaming stdout/stderr to the live log with ANSI/secret
///         redaction.</item>
///   <item>The silence watchdog that flags genuine hangs as
///         <see cref="DockerShellResult.TimedOutBySilence"/> so the
///         Doctor can propose a strategy switch (remote ACR build,
///         skip step, etc.).</item>
///   <item>The az/azd pre-warm shim that side-steps Go's hard 10-second
///         <c>AzureCLICredential</c> deadline on cold container starts
///         (still useful on the FIRST exec; near-free afterwards thanks
///         to disk + token cache reuse inside the same container).</item>
/// </list>
/// </summary>
public sealed class DockerShellTool
{
    private readonly SandboxSession _session;
    private readonly Action<string, string> _onLog; // (level, line)

    private static readonly Regex Redactor =
        new(@"(Bearer\s+[A-Za-z0-9\-_\.]+)|([A-Za-z0-9]{32,})", RegexOptions.Compiled);

    private static readonly Regex AnsiEscape =
        new(@"\x1B\[[0-9;]*[A-Za-z]|\\033\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public DockerShellTool(SandboxSession session, Action<string, string> onLog)
    {
        _session = session;
        _onLog = onLog;
    }

    public async Task<DockerShellResult> RunAsync(
        string shellCommand,
        string containerCwd,
        IDictionary<string, string>? envVars,
        TimeSpan timeout,
        CancellationToken ct,
        TimeSpan? silenceBudget = null,
        int? tailSize = null)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(timeout);

        var effectiveSilence = silenceBudget ?? TimeSpan.FromMinutes(15);
        var lastActivity = DateTime.UtcNow;
        var silenceTriggered = false;
        using var silenceCts = CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token);
        var silenceWatcher = Task.Run(async () =>
        {
            try
            {
                while (!silenceCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), silenceCts.Token);
                    var idle = DateTime.UtcNow - lastActivity;
                    if (idle >= effectiveSilence)
                    {
                        silenceTriggered = true;
                        _onLog("warn",
                            $"Step produced no output for {idle.TotalMinutes:0} min " +
                            $"(silence budget {effectiveSilence.TotalMinutes:0} min). " +
                            "Treating as a hang and aborting the step so the Doctor can " +
                            "propose a different strategy (e.g. remote ACR build).");
                        try { stepCts.Cancel(); } catch { /* already cancelled */ }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        }, silenceCts.Token);

        // Pre-warm hook for 'az'/'azd'. Go's AzureCLICredential has a hard
        // 10 s deadline on the 'az account get-access-token' subprocess
        // call; a cold sandbox needs 10-15 s for Python + azure-cli +
        // MSAL fetch. With the long-lived session container the first
        // exec pays this cost; subsequent execs reuse the hot Python and
        // cached token. Conservative: if in doubt, prewarm � cheap when
        // already warm, vital when not.
        var wrapped = NeedsAzPrewarm(shellCommand)
            ? "az account show --query id -o tsv >/dev/null 2>&1 || true; " +
              "az account get-access-token --resource https://management.core.windows.net/ " +
              "  --query expiresOn -o tsv >/dev/null 2>&1 || true; " +
              shellCommand
            : shellCommand;

        var cwd = "/workspace/" + (containerCwd ?? "").TrimStart('.', '/');
        if (cwd.EndsWith('/')) cwd = cwd.TrimEnd('/');
        if (cwd == "/workspace/") cwd = "/workspace";

        // Default tail of 40 lines is enough for most steps and keeps
        // memory bounded; callers that need to scrape full structured
        // output (e.g. AzdEnvLoader parsing 30+ azd env entries that
        // would otherwise be evicted by trailing azd progress/log
        // lines) can ask for a much larger window.
        var effectiveTailSize = tailSize ?? 40;
        var tail = new Queue<string>(effectiveTailSize);

        int exitCode;
        try
        {
            exitCode = await _session.ExecAsync(
                wrapped,
                cwd,
                envVars,
                onStdout: o =>
                {
                    lastActivity = DateTime.UtcNow;
                    var line = Sanitize(o);
                    _onLog("info", line);
                    AppendTail(tail, line, effectiveTailSize);
                },
                onStderr: e =>
                {
                    lastActivity = DateTime.UtcNow;
                    var line = Sanitize(e);
                    _onLog("err", line);
                    AppendTail(tail, line, effectiveTailSize);
                },
                stepCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!silenceTriggered) throw;
            exitCode = -1;
        }

        silenceCts.Cancel();
        try { await silenceWatcher; } catch { /* expected */ }

        return new DockerShellResult(exitCode, string.Join('\n', tail))
        {
            TimedOutBySilence = silenceTriggered
        };
    }

    private static string Sanitize(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        var clean = AnsiEscape.Replace(line, string.Empty);
        return Redactor.Replace(clean, "***");
    }

    private static void AppendTail(Queue<string> tail, string line, int max)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        tail.Enqueue(line);
        while (tail.Count > max) tail.Dequeue();
    }

    private static bool NeedsAzPrewarm(string shellCommand)
    {
        if (string.IsNullOrWhiteSpace(shellCommand)) return false;
        var lc = shellCommand.ToLowerInvariant();
        return lc.Contains("azd ") || lc.StartsWith("azd")
            || lc.Contains(" az ") || lc.StartsWith("az ")
            || lc.Contains("$(az ") || lc.Contains("`az ");
    }
}
