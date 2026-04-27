using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AgentStationHub.Services;

/// <summary>
/// Fetches live information about Microsoft Azure AI Toolkit for Visual Studio Code:
/// the official repository metadata and a list of related sample repositories from
/// the GitHub Search API.
/// </summary>
public class AzureAIToolkitService
{
    private readonly IHttpClientFactory _httpFactory;

    // Process-wide cache to avoid double-fetch caused by Blazor pre-render +
    // interactive render running OnInitializedAsync twice, and to keep the
    // page snappy across navigations. Refreshed on demand by the Scan button.
    private static ToolkitOverview? _cached;
    private static DateTime _cachedAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public AzureAIToolkitService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<ToolkitOverview> GetOverviewAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;

            var fresh = await FetchAsync(ct);
            _cached = fresh;
            _cachedAtUtc = DateTime.UtcNow;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ToolkitOverview> FetchAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("github");

        // Run main repo lookup and the 3 search queries in parallel to cut latency.
        string[] queries =
        [
            "\"ai toolkit\" vscode in:name,description,readme",
            "topic:ai-toolkit",
            "\"azure ai toolkit\" in:name,description,readme"
        ];

        Task<RepoInfo?> mainTask = SafeGetAsync<RepoInfo>(http, "repos/microsoft/vscode-ai-toolkit", ct);

        var searchTasks = queries
            .Select(q => SafeGetAsync<SearchResponse>(
                http,
                $"search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page=8",
                ct))
            .ToArray();

        await Task.WhenAll(new Task[] { mainTask }.Concat(searchTasks));

        RepoInfo? main = mainTask.Result;

        var samples = new List<RepoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in searchTasks)
        {
            if (t.Result?.Items is null) continue;
            foreach (var r in t.Result.Items)
            {
                if (r.HtmlUrl is null || !seen.Add(r.HtmlUrl)) continue;
                if (main is not null && string.Equals(r.HtmlUrl, main.HtmlUrl, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsEnglish(r)) continue;

                samples.Add(r);
                if (samples.Count >= 12) break;
            }
            if (samples.Count >= 12) break;
        }

        return new ToolkitOverview(main, samples);
    }

    private static async Task<T?> SafeGetAsync<T>(HttpClient http, string url, CancellationToken ct) where T : class
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            return await http.GetFromJsonAsync<T>(url, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    public sealed record ToolkitOverview(RepoInfo? MainRepo, IReadOnlyList<RepoInfo> Samples);

    /// <summary>
    /// Heuristic English-only filter: rejects repositories whose name or
    /// description contains characters from CJK, Cyrillic, Arabic, Hebrew,
    /// Devanagari and other non-Latin Unicode blocks. A small tolerance is
    /// allowed so that e.g. an English description with one stray symbol is
    /// not dropped.
    /// </summary>
    private static bool IsEnglish(RepoInfo r)
    {
        var text = $"{r.Name} {r.Description}";
        if (string.IsNullOrWhiteSpace(text)) return true;

        int nonLatin = 0, total = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsDigit(ch) || char.IsSymbol(ch))
                continue;

            total++;
            int cp = ch;
            // CJK Unified Ideographs, Hiragana, Katakana, Hangul, Bopomofo
            bool isCjk = (cp >= 0x3040 && cp <= 0x30FF)
                      || (cp >= 0x3400 && cp <= 0x4DBF)
                      || (cp >= 0x4E00 && cp <= 0x9FFF)
                      || (cp >= 0xAC00 && cp <= 0xD7AF)
                      || (cp >= 0xF900 && cp <= 0xFAFF)
                      || (cp >= 0xFF00 && cp <= 0xFFEF);
            // Cyrillic, Arabic, Hebrew, Devanagari, Thai, Greek
            bool isOther = (cp >= 0x0400 && cp <= 0x04FF)
                        || (cp >= 0x0590 && cp <= 0x06FF)
                        || (cp >= 0x0900 && cp <= 0x097F)
                        || (cp >= 0x0E00 && cp <= 0x0E7F)
                        || (cp >= 0x0370 && cp <= 0x03FF);

            if (isCjk || isOther) nonLatin++;
        }

        if (total == 0) return true;
        // Reject if more than 5% of meaningful characters are non-Latin
        return (double)nonLatin / total < 0.05;
    }

    public sealed record RepoInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("stargazers_count")] int StargazersCount,
        [property: JsonPropertyName("forks_count")] int ForksCount,
        [property: JsonPropertyName("topics")] List<string>? Topics,
        [property: JsonPropertyName("owner")] OwnerInfo? Owner);

    public sealed record OwnerInfo(
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

    private sealed record SearchResponse(
        [property: JsonPropertyName("items")] List<RepoInfo>? Items);
}

/// <summary>
/// Static curated links to official Microsoft resources about Azure AI Toolkit.
/// </summary>
public static class AzureAIToolkitLinks
{
    public static readonly (string Title, string Description, string Url, string Icon)[] Resources =
    [
        ("Azure AI Toolkit for VS Code",
         "Official Visual Studio Marketplace extension. Install it directly in VS Code.",
         "https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio",
         "bi-box-seam-fill"),

        ("Official documentation",
         "VS Code docs for the AI Toolkit: overview, setup and feature walkthroughs.",
         "https://code.visualstudio.com/docs/intelligentapps/overview",
         "bi-book-fill"),

        ("GitHub repository",
         "Source code, issue tracker and roadmap: microsoft/vscode-ai-toolkit.",
         "https://github.com/microsoft/vscode-ai-toolkit",
         "bi-github"),

        ("Model Catalog",
         "Browse, download and run hundreds of models from Azure AI, Hugging Face, GitHub and Ollama.",
         "https://code.visualstudio.com/docs/intelligentapps/models",
         "bi-boxes"),

        ("Playground & prompt testing",
         "Interactive playground to test prompts, compare models and iterate on agents locally.",
         "https://code.visualstudio.com/docs/intelligentapps/playground",
         "bi-play-circle-fill"),

        ("Agent Builder",
         "Visually design, test and export agents with tools and MCP servers, ready for Azure AI Foundry.",
         "https://code.visualstudio.com/docs/intelligentapps/agentbuilder",
         "bi-diagram-3-fill"),

        ("Fine-tuning workflow",
         "Fine-tune open-source models locally or on Azure with ready-made templates.",
         "https://code.visualstudio.com/docs/intelligentapps/finetune",
         "bi-sliders"),

        ("Evaluation & bulk-run",
         "Evaluate agent responses against datasets with built-in and custom metrics.",
         "https://code.visualstudio.com/docs/intelligentapps/evaluation",
         "bi-graph-up-arrow"),
    ];
}
