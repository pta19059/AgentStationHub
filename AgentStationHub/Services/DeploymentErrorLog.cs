using System.Text.Json;
using AgentStationHub.Models;

namespace AgentStationHub.Services;

/// <summary>
/// Lightweight JSONL append-only log of terminal-failure deployments.
/// One line per failure, written to
/// <c>{LocalAppData}/AgentStationHub/errors.jsonl</c> which is mapped to
/// the <c>agentichub-state</c> Docker volume. Designed to be tailed from
/// the host (e.g. via <c>docker exec agentichub-app cat
/// /root/.local/share/AgentStationHub/errors.jsonl</c>) for quick
/// out-of-band triage without parsing full per-session JSON blobs.
/// </summary>
public sealed class DeploymentErrorLog
{
    private readonly ILogger<DeploymentErrorLog> _log;
    private readonly string _path;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DeploymentErrorLog(ILogger<DeploymentErrorLog> log)
    {
        _log = log;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStationHub");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "errors.jsonl");
    }

    public string Path_ => _path;

    /// <summary>
    /// Append a single JSON line summarising the failure. Last ~2 KB of
    /// log tail is included so the host can grep for error signatures.
    /// </summary>
    public void Record(DeploymentSession s)
    {
        try
        {
            // Last ~30 log entries.
            var tail = s.Logs
                .TakeLast(30)
                .Select(l => $"[{l.AtUtc:HH:mm:ss}] {l.Level}: {l.Message}")
                .ToList();

            var entry = new
            {
                ts = DateTime.UtcNow,
                id = s.Id,
                status = s.Status.ToString(),
                repoUrl = s.RepoUrl,
                samplePath = s.SamplePath,
                errorMessage = s.ErrorMessage,
                tail,
            };

            var line = JsonSerializer.Serialize(entry, JsonOpts);
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to append failure record for session {Id}.", s.Id);
        }
    }
}
