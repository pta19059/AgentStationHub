using System.Text;
using System.Text.Json;
using CliWrap;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Background poller that runs alongside a long-running 'azd up' / 'azd
/// provision' / 'azd deploy' step and emits periodic progress snapshots to
/// the Live log by calling 'az deployment sub list' inside an ephemeral
/// container that shares the sandbox's persistent Azure auth volume.
///
/// Problem solved: while azd provisions dozens of child resources it can
/// stay silent for several minutes (package upload, ACR push, Container
/// App rollout). Without external signal the UI looks frozen. This watcher
/// fills the silence with actionable status: "N deployments, X succeeded,
/// Y running — new: <names>" every 30 seconds.
///
/// The poller runs under a cancellation token linked to the step's CT so
/// it dies cleanly as soon as the step returns.
/// </summary>
public sealed class DeploymentProgressWatcher
{
    private readonly string _sandboxImage;
    private readonly string _envName;
    private readonly Action<string, string> _log;
    private readonly HashSet<string> _seenDeployments = new(StringComparer.Ordinal);
    private DateTime _startTime;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public DeploymentProgressWatcher(
        string sandboxImage,
        string envName,
        Action<string, string> log)
    {
        _sandboxImage = sandboxImage;
        _envName = envName;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _startTime = DateTime.UtcNow;

        // Give azd ~30s to finish packaging / upload before we start polling
        // Azure — querying too early just returns "no deployments found".
        try { await Task.Delay(InitialDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // A single transient failure (network, auth refresh) must not
                // kill the watcher; the next cycle will retry.
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // JMESPath query: find all subscription-level deployments that match
        // the azd environment name, sort by timestamp, and project a compact
        // view of each one.
        var query =
            $"[?contains(name, '{_envName}')] | sort_by(@, &properties.timestamp) | " +
            "[].{name:name, state:properties.provisioningState, ts:properties.timestamp}";

        var args = new List<string>
        {
            "run", "--rm",
            "--dns", "1.1.1.1",
            "--dns", "8.8.8.8",
            "--memory", "1g",
        };
        args.AddRange(SandboxAzureAuth.VolumeMountArgs());
        args.Add(_sandboxImage);
        args.Add("az");
        args.Add("deployment");
        args.Add("sub");
        args.Add("list");
        args.Add("--query");
        args.Add(query);
        args.Add("-o");
        args.Add("json");
        args.Add("--only-show-errors");

        var stdout = new StringBuilder();
        var result = await Cli.Wrap("docker")
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout, System.Text.Encoding.UTF8))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0) return;

        var json = stdout.ToString().Trim();
        if (string.IsNullOrEmpty(json) || json == "[]") return;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        var items = doc.RootElement.EnumerateArray().ToList();
        if (items.Count == 0) return;

        int succeeded = 0, running = 0, failed = 0;
        var newNames = new List<string>();
        foreach (var item in items)
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            switch (state)
            {
                case "Succeeded": succeeded++; break;
                case "Running":   running++;   break;
                case "Failed":    failed++;    break;
            }
            if (_seenDeployments.Add(name)) newNames.Add(name);
        }

        var elapsed = DateTime.UtcNow - _startTime;
        var elapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        var summary = $"[progress {elapsedText}] {items.Count} azd deployment(s): " +
                      $"{succeeded} succeeded, {running} running, {failed} failed";
        if (newNames.Count > 0)
        {
            var preview = newNames.Take(4).ToList();
            var suffix = newNames.Count > 4 ? $" (+{newNames.Count - 4} more)" : "";
            summary += $" — new: {string.Join(", ", preview)}{suffix}";
        }

        _log("info", summary);
    }
}
