using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStationHub.Services;

/// <summary>
/// Pragmatic memory layer for the agent pipeline. Two concerns live
/// behind one service:
///
/// 1. <b>Session turns</b> � raw per-deploy conversation log (agent name,
///    input, output, timestamp). Scoped to a single
///    <c>DeploymentSession</c>, used mostly for debugging and to render
///    a "trace" side-panel in the UI. Kept small (truncated input/output)
///    and pruned on a sliding window to avoid disk bloat.
///
/// 2. <b>Insights</b> � cross-session structured facts distilled from
///    completed deploys. These are durable learning signals:
///    <list type="bullet">
///    <item>"repo X needs AZURE_OPENAI_SERVICE_LOCATION=swedencentral"
///          (discovered after a successful deploy)</item>
///    <item>"repo X Bicep allowed regions:
///          [eastus2, westus2, ...]"</item>
///    <item>"global: gpt-4o-realtime-preview is deprecated"</item>
///    </list>
///    Insights are consulted BEFORE planning a new deploy, and passed as
///    hints to the Strategist / Doctor so they do not rediscover the same
///    constraints every time.
///
/// The store is disk-backed (JSON under <c>%LOCALAPPDATA%/AgentStationHub</c>)
/// so both session turns and insights survive process restarts. Best-effort
/// I/O: a corrupt file is logged but never blocks the app.
///
/// This class is INTENTIONALLY dependency-free and self-contained so the
/// sandbox runner could host a trimmed variant in the future without
/// pulling half of the main app.
/// </summary>
public sealed class AgentMemoryStore
{
    private readonly string _sessionsDir;
    private readonly string _insightsPath;
    private readonly object _insightsLock = new();

    // The insights index is cached in-memory and synchronously flushed on
    // every write. Typical size is < 100 entries so full rewrite is cheap
    // and we avoid the complexity of an append-log format.
    private List<AgentInsight> _insights = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public AgentMemoryStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStationHub");
        _sessionsDir = Path.Combine(root, "agent-memory", "sessions");
        _insightsPath = Path.Combine(root, "agent-memory", "insights.json");
        Directory.CreateDirectory(_sessionsDir);
        LoadInsights();
        PruneOldSessions(TimeSpan.FromDays(30));
    }

    // --------------------------------------------------------------------
    // SESSION TURNS
    // --------------------------------------------------------------------

    /// <summary>
    /// Records a single turn in a session's history. Inputs and outputs
    /// are truncated to keep individual session files small (the sandbox
    /// team can emit tens of KB of JSON per agent, which would blow up
    /// disk fast across many deploys).
    /// </summary>
    public void RecordTurn(string sessionId, string agent, string role, string content)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(agent)) return;

        var path = SessionPath(sessionId);
        var turn = new AgentTurn(
            At: DateTimeOffset.UtcNow,
            Agent: agent,
            Role: role,
            Content: Truncate(content, 4_000));

        // Sessions are rarely accessed concurrently (one deploy = one
        // orchestrator task), so a file-level lock keyed on the path is
        // sufficient � no global contention.
        lock (SessionLock(sessionId))
        {
            var turns = LoadSession(path);
            turns.Add(turn);
            // Cap each session at 500 turns to bound growth on pathological
            // loops (100 Doctor retries etc.).
            if (turns.Count > 500) turns.RemoveRange(0, turns.Count - 500);
            try
            {
                var json = JsonSerializer.Serialize(turns, JsonOpts);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Best-effort: the app must keep running even when the
                // state directory becomes unwritable (full disk, ACLs).
            }
        }
    }

    public IReadOnlyList<AgentTurn> GetSessionTurns(string sessionId)
    {
        var path = SessionPath(sessionId);
        lock (SessionLock(sessionId)) return LoadSession(path);
    }

    // --------------------------------------------------------------------
    // INSIGHTS (cross-session)
    // --------------------------------------------------------------------

    /// <summary>
    /// Upserts a structured fact learned about a repository (or globally
    /// when <paramref name="repoUrl"/> is null). Repeat calls with the
    /// same <paramref name="key"/> REPLACE the previous value and refresh
    /// the timestamp � the store never grows unboundedly for a given key.
    /// </summary>
    public void UpsertInsight(string? repoUrl, string key, string value, double confidence = 1.0)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_insightsLock)
        {
            _insights.RemoveAll(i =>
                string.Equals(i.RepoUrl, repoUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Key,     key,     StringComparison.OrdinalIgnoreCase));
            _insights.Add(new AgentInsight(
                At: DateTimeOffset.UtcNow,
                RepoUrl: repoUrl,
                Key: key,
                Value: Truncate(value, 1_500),
                Confidence: Math.Clamp(confidence, 0, 1)));
            SaveInsights();
        }
    }

    /// <summary>
    /// Returns all insights relevant to a deploy of <paramref name="repoUrl"/>:
    /// the repo-specific ones (exact URL match, case-insensitive) plus all
    /// global insights (stored with <c>RepoUrl == null</c>). Global facts
    /// tend to be model-lifecycle signals ("gpt-4o-audio-preview deprecated")
    /// that apply to every deploy until Microsoft rotates them.
    /// </summary>
    public IReadOnlyList<AgentInsight> GetRelevantInsights(string repoUrl)
    {
        lock (_insightsLock)
        {
            return _insights
                .Where(i => i.RepoUrl is null
                         || string.Equals(i.RepoUrl, repoUrl, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.At)
                .ToList();
        }
    }

    public IReadOnlyList<AgentInsight> GetAllInsights()
    {
        lock (_insightsLock) return _insights.ToList();
    }

    /// <summary>
    /// Idempotent seed: only (re)writes the insight when it is missing or
    /// its <paramref name="value"/> differs from what is already stored.
    /// Prevents churning the <c>At</c> timestamp on every process boot
    /// when the orchestrator plants built-in global lessons.
    /// </summary>
    public void SeedGlobalInsightIfChanged(string key, string value, double confidence = 1.0)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_insightsLock)
        {
            var existing = _insights.FirstOrDefault(i =>
                i.RepoUrl is null &&
                string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && string.Equals(existing.Value, value, StringComparison.Ordinal))
                return; // already seeded with the current value
        }
        UpsertInsight(null, key, value, confidence);
    }

    // --------------------------------------------------------------------
    // INTERNALS
    // --------------------------------------------------------------------

    private string SessionPath(string sessionId) =>
        Path.Combine(_sessionsDir, $"{Sanitise(sessionId)}.json");

    private static readonly Dictionary<string, object> _sessionLocks = new();
    private static object SessionLock(string sessionId)
    {
        lock (_sessionLocks)
        {
            if (!_sessionLocks.TryGetValue(sessionId, out var l))
                _sessionLocks[sessionId] = l = new object();
            return l;
        }
    }

    private static List<AgentTurn> LoadSession(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new();
            return JsonSerializer.Deserialize<List<AgentTurn>>(json, JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    private void LoadInsights()
    {
        try
        {
            if (!File.Exists(_insightsPath)) return;
            var json = File.ReadAllText(_insightsPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            _insights = JsonSerializer.Deserialize<List<AgentInsight>>(json, JsonOpts) ?? new();
        }
        catch { _insights = new(); }
    }

    private void SaveInsights()
    {
        try
        {
            var dir = Path.GetDirectoryName(_insightsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_insightsPath, JsonSerializer.Serialize(_insights, JsonOpts));
        }
        catch
        {
            // Persistence is a nice-to-have; if disk writes fail the
            // in-memory list still serves the current process.
        }
    }

    private void PruneOldSessions(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var f in Directory.EnumerateFiles(_sessionsDir, "*.json"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

    private static string Sanitise(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
}

public sealed record AgentTurn(
    DateTimeOffset At,
    string Agent,
    /// <summary>'input' | 'output' | 'thinking' | 'rationale' | ...</summary>
    string Role,
    string Content);

public sealed record AgentInsight(
    DateTimeOffset At,
    /// <summary>Null for global insights (apply to any deploy).</summary>
    string? RepoUrl,
    /// <summary>Stable identifier, e.g. "azd.required.service-location" or "model.deprecated".</summary>
    string Key,
    string Value,
    /// <summary>0-1 confidence; &gt;= 0.8 means the Doctor can rely on it without re-verifying.</summary>
    double Confidence);
