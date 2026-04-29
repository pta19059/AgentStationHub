using System.Diagnostics;
using System.Text.Json;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Live ground-truth probe for the AOAI model catalog in a given Azure
/// region. Wraps `az cognitiveservices model list -l &lt;region&gt;` and
/// caches the result in-process for 24 h.
///
/// Why: the orchestrator's hand-coded model/version replacement table in
/// <c>TryAutoPatchEscalation</c> Pattern C ages out every time Azure
/// retires a model or version. Patching the table after the fact means
/// at least one user hits a 13-iteration self-healing loop before the
/// fix lands. A live catalog turns "did Azure deprecate X?" from a
/// guess into a deterministic check, and feeds the EscalationResolver
/// agent (in-process and Foundry-hosted) the authoritative list of
/// what's currently deployable in the target region — so its proposed
/// fix is grounded instead of hallucinated.
///
/// We use the az CLI rather than the management.azure.com REST endpoint
/// because (a) the CLI is already on PATH inside the Hub container,
/// (b) it inherits the orchestrator's <c>DefaultAzureCredential</c>
/// login (no extra token plumbing), and (c) it produces the exact same
/// JSON shape the rest of the codebase already parses.
/// </summary>
public sealed class AzureModelCatalogProbe
{
    private readonly ILogger<AzureModelCatalogProbe> _log;
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public AzureModelCatalogProbe(ILogger<AzureModelCatalogProbe> log)
    {
        _log = log;
    }

    /// <summary>
    /// Returns the supported AOAI models for the given region as a
    /// human-readable text block intended to be embedded in an LLM
    /// prompt. Empty string when the probe fails (CLI missing,
    /// auth missing, region unknown, etc.) — never throws.
    /// </summary>
    public async Task<string> GetCatalogPromptBlockAsync(
        string region,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(region)) return "";
        var entries = await GetCatalogAsync(region, ct);
        if (entries.Count == 0) return "";

        // Group by name → list of "version (sku)". Stable, compact.
        var byName = entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"AZURE OPENAI MODELS AVAILABLE IN '{region}' (live `az cognitiveservices model list` snapshot):");
        foreach (var g in byName)
        {
            var versions = g
                .Select(e => string.IsNullOrWhiteSpace(e.Sku)
                    ? e.Version
                    : $"{e.Version} [{e.Sku}]")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase);
            sb.Append("  - ").Append(g.Key).Append(": ");
            sb.AppendLine(string.Join(", ", versions));
        }
        sb.AppendLine("(Use ONLY (name, version) pairs that appear above. Anything else WILL fail ARM validation.)");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the structured catalog. Empty list on any failure.
    /// Caches per-region for 24 h.
    /// </summary>
    public async Task<IReadOnlyList<ModelEntry>> GetCatalogAsync(
        string region,
        CancellationToken ct)
    {
        var key = region.Trim().ToLowerInvariant();
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var hit) &&
                DateTimeOffset.UtcNow - hit.FetchedAt < CacheTtl)
            {
                return hit.Entries;
            }
        }
        finally { _cacheGate.Release(); }

        var fresh = await FetchAsync(key, ct);
        await _cacheGate.WaitAsync(ct);
        try
        {
            _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, fresh);
        }
        finally { _cacheGate.Release(); }
        return fresh;
    }

    private async Task<IReadOnlyList<ModelEntry>> FetchAsync(
        string region,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("az",
                $"cognitiveservices model list -l {region} -o json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<ModelEntry>();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            using var reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            });

            await p.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (p.ExitCode != 0)
            {
                _log.LogInformation(
                    "az cognitiveservices model list -l {Region} exit={Code} stderr={Err}",
                    region, p.ExitCode, Truncate(stderr, 400));
                return Array.Empty<ModelEntry>();
            }

            return Parse(stdout);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation(ex,
                "AzureModelCatalogProbe: az invocation failed for region {Region}", region);
            return Array.Empty<ModelEntry>();
        }
    }

    private static IReadOnlyList<ModelEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ModelEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<ModelEntry>();

            var list = new List<ModelEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("model", out var m)) continue;
                if (!m.TryGetProperty("name", out var nameEl)) continue;
                if (!m.TryGetProperty("version", out var verEl)) continue;
                var name = nameEl.GetString() ?? "";
                var version = verEl.GetString() ?? "";
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                    continue;

                string? sku = null;
                if (m.TryGetProperty("skus", out var skusEl) &&
                    skusEl.ValueKind == JsonValueKind.Array)
                {
                    var skuNames = skusEl.EnumerateArray()
                        .Select(s => s.TryGetProperty("name", out var nm) ? nm.GetString() : null)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (skuNames.Length > 0) sku = string.Join("/", skuNames);
                }

                // Filter out non-OpenAI / non-text-image-audio kinds when
                // the entry is clearly not an Azure OpenAI deployment
                // candidate. The CLI returns whisper / dall-e / embeddings
                // alongside chat models, all of which are valid.
                list.Add(new ModelEntry(name, version, sku));
            }
            return list;
        }
        catch
        {
            return Array.Empty<ModelEntry>();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    public sealed record ModelEntry(string Name, string Version, string? Sku);
    private sealed record CacheEntry(
        DateTimeOffset FetchedAt,
        IReadOnlyList<ModelEntry> Entries);
}
