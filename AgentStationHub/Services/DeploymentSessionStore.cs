using System.Text.Json;
using AgentStationHub.Models;

namespace AgentStationHub.Services;

/// <summary>
/// JSON-file backed persistence for <see cref="DeploymentSession"/>.
/// Each session lives in its own file under
/// <c>%LOCALAPPDATA%/AgentStationHub/sessions/&lt;id&gt;.json</c>
/// so a crash / container-restart of the main app does NOT lose the
/// state of a running deploy: on boot the orchestrator rehydrates the
/// dictionary, marks non-terminal sessions as <c>Failed</c> with an
/// "Interrupted" message (the background pipeline task cannot be
/// resurrected — only the SignalR view of progress-so-far), and the
/// browser's <c>localStorage</c>-driven resume still works.
///
/// Writes are coalesced per-session: while a save is in flight for a
/// given id, additional "SaveLater" calls just mark the session dirty;
/// when the current save completes and the dirty flag is set, one
/// extra save is kicked off. This bounds disk churn to ~1 write per
/// session every few hundred ms even under high log-rate (docker build
/// output streams thousands of lines a minute).
/// </summary>
public sealed class DeploymentSessionStore
{
    private readonly ILogger<DeploymentSessionStore> _log;
    private readonly string _dir;

    // Per-session write serialisation. Each entry owns:
    //  - a SemaphoreSlim(1,1) acting as a one-at-a-time mutex
    //  - a 'dirty' flag set by SaveLater while the mutex is held
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionWriter> _writers = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public DeploymentSessionStore(ILogger<DeploymentSessionStore> log)
    {
        _log = log;
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStationHub", "sessions");
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    // ------------------------------------------------------------------ load

    /// <summary>
    /// Reads every <c>*.json</c> in the sessions directory and returns
    /// rebuilt <see cref="DeploymentSession"/> instances. Corrupt files
    /// are logged and skipped so a single bad payload doesn't break
    /// startup.
    /// </summary>
    public IEnumerable<DeploymentSession> LoadAll()
    {
        if (!System.IO.Directory.Exists(_dir)) yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            DeploymentSession? s = null;
            try
            {
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<SessionDto>(json, JsonOpts);
                if (dto is not null) s = dto.ToSession();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to rehydrate session file '{Path}' (skipped).", path);
            }
            if (s is not null) yield return s;
        }
    }

    public void Delete(string sessionId)
    {
        try
        {
            var path = Path.Combine(_dir, sessionId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Delete session file failed for {Id}", sessionId);
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Schedules a best-effort async write. Safe to call from hot paths
    /// (every log line): writes are coalesced so bursts don't thrash
    /// the disk.
    /// </summary>
    public void SaveLater(DeploymentSession session)
    {
        var w = _writers.GetOrAdd(session.Id, _ => new SessionWriter());
        w.Dirty = true;
        // Fire-and-forget; exceptions are swallowed inside DrainAsync.
        _ = DrainAsync(session, w);
    }

    private async Task DrainAsync(DeploymentSession session, SessionWriter w)
    {
        // Non-blocking try-take: if another DrainAsync is already in
        // flight, it will observe Dirty=true when it finishes and loop.
        if (!await w.Mutex.WaitAsync(0)) return;
        try
        {
            while (w.Dirty)
            {
                w.Dirty = false;
                await WriteOnceAsync(session);
            }
        }
        finally
        {
            w.Mutex.Release();
        }
    }

    private async Task WriteOnceAsync(DeploymentSession session)
    {
        try
        {
            var dto = SessionDto.From(session);
            var path = Path.Combine(_dir, session.Id + ".json");
            var tmp  = path + ".tmp";
            // Atomic-ish write: serialise to a sibling .tmp then rename.
            // Avoids half-written files if the process dies mid-write.
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, dto, JsonOpts);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Persist session {Id} failed (non-fatal; will retry on next change).",
                session.Id);
        }
    }

    private sealed class SessionWriter
    {
        public readonly SemaphoreSlim Mutex = new(1, 1);
        public volatile bool Dirty;
    }

    // ------------------------------------------------------------------ DTO

    /// <summary>
    /// Plain serialisable projection of <see cref="DeploymentSession"/>
    /// — strips the live-only pieces (<c>ApprovalTcs</c>, <c>Cts</c>)
    /// that don't round-trip through JSON, and keeps the on-disk format
    /// decoupled from potentially non-serialisable future additions to
    /// the session class.
    /// </summary>
    private sealed class SessionDto
    {
        public string Id { get; set; } = "";
        public string RepoUrl { get; set; } = "";
        public string WorkDir { get; set; } = "";
        public string? SamplePath { get; set; }
        public string AzureLocation { get; set; } = "";
        public DeploymentStatus Status { get; set; }
        public DeploymentPlan? Plan { get; set; }
        public List<LogEntry> Logs { get; set; } = new();
        public string? FinalEndpoint { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public static SessionDto From(DeploymentSession s) => new()
        {
            Id            = s.Id,
            RepoUrl       = s.RepoUrl,
            WorkDir       = s.WorkDir,
            SamplePath    = s.SamplePath,
            AzureLocation = s.AzureLocation,
            Status        = s.Status,
            Plan          = s.Plan,
            // Snapshot the logs under the list's own lock-free assumption:
            // List<T>.Add is called only from the orchestrator loop, which
            // also drives SaveLater, so we are sequentially consistent on
            // the writer side.
            Logs          = s.Logs.ToList(),
            FinalEndpoint = s.FinalEndpoint,
            ErrorMessage  = s.ErrorMessage,
            CreatedAtUtc  = s.CreatedAtUtc,
        };

        public DeploymentSession ToSession()
        {
            // DeploymentSession auto-generates a new Id at construction,
            // but the public 'Id' is init-only. We rebuild via reflection
            // on the backing field so the rehydrated session keeps the
            // same identifier the browser still has in localStorage.
            var s = new DeploymentSession
            {
                RepoUrl       = RepoUrl,
                WorkDir       = WorkDir,
                SamplePath    = SamplePath,
                AzureLocation = AzureLocation,
                Status        = Status,
                Plan          = Plan,
                FinalEndpoint = FinalEndpoint,
                ErrorMessage  = ErrorMessage,
            };
            // Replace the generated Id with the persisted one.
            var idProp = typeof(DeploymentSession).GetProperty(nameof(DeploymentSession.Id));
            var backing = typeof(DeploymentSession).GetField(
                "<Id>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            backing?.SetValue(s, Id);
            var createdField = typeof(DeploymentSession).GetField(
                "<CreatedAtUtc>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            createdField?.SetValue(s, CreatedAtUtc);
            foreach (var e in Logs) s.Logs.Add(e);
            return s;
        }
    }
}
