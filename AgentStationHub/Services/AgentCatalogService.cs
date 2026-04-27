using AgentStationHub.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStationHub.Services;

/// <summary>
/// Provides a curated catalog of the most relevant agent samples
/// sourced from public GitHub repositories, ready to be deployed
/// onto Copilot Studio and/or Azure AI Foundry.
/// </summary>
public class AgentCatalogService
{
    private readonly IHttpClientFactory? _httpFactory;

    /// <summary>
    /// Persists only the agents discovered via 'Scan the web' (not the
    /// hardcoded seed) to local app data so they survive app restarts.
    /// The seed stays in code because it is the trusted curated baseline.
    /// </summary>
    private readonly string _discoveredStorePath;
    private readonly HashSet<string> _seedIds;
    private readonly object _persistenceLock = new();

    private static readonly JsonSerializerOptions DiscoveredJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AgentCatalogService(IHttpClientFactory httpFactory)
        : this()
    {
        _httpFactory = httpFactory;
    }

    public AgentCatalogService()
    {
        _discoveredStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStationHub",
            "discovered-agents.json");
        // Snapshot the seed ids BEFORE we merge persisted discoveries so
        // we can always distinguish 'user-scanned' entries from 'curated'.
        _seedIds = new HashSet<string>(_samples.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        LoadDiscovered();
    }
    // Copilot Studio portal (home) - used as fallback when there is no public
    // template-import URL. Copilot Studio does not currently expose an import
    // endpoint via query string, so the user is taken to the portal to
    // manually import the solution from the repo.
    private const string CopilotStudioPortal = "https://copilotstudio.microsoft.com/";

    // Catalog based exclusively on real public GitHub repositories
    // (Microsoft / Azure-Samples). Star counts are approximate at curation
    // time and can be refreshed dynamically via the GitHub API in the future.
    private readonly List<AgentSample> _samples =
    [
        // ---------------- MEDICAL ----------------
        new AgentSample(
            Id: "healthcare-agent-orchestrator",
            Name: "Healthcare Agent Orchestrator",
            Description: "Multi-agent orchestration for clinical workflows: tumor-board, medical record summarization, patient triage. Built on Azure AI Foundry + Semantic Kernel. Includes Bicep infra deployable with azd.",
            Category: AgentCategory.Medical,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/healthcare-agent-orchestrator",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["healthcare", "tumor-board", "semantic-kernel", "multi-agent", "azd"],
            Stars: 350,
            Author: "Azure-Samples"),

        new AgentSample(
            Id: "azure-search-openai-demo-medical",
            Name: "Medical Knowledge RAG (Chat + Citations)",
            Description: "End-to-end RAG template (Azure AI Search + Azure OpenAI) used as a base for Q&A on clinical documents and medical literature. One-liner `azd up` to deploy.",
            Category: AgentCategory.Medical,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/azure-search-openai-demo",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["rag", "azure-openai", "azure-search", "citations", "azd"],
            Stars: 7200,
            Author: "Azure-Samples"),

        // ---------------- LEGAL ----------------
        new AgentSample(
            Id: "gpt-rag-legal",
            Name: "Enterprise RAG for Legal Documents",
            Description: "Enterprise RAG accelerator (Azure/GPT-RAG) configurable for contract review, clause extraction and Q&A over internal case-law. Ships with full IaC infra.",
            Category: AgentCategory.Legal,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure/GPT-RAG",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["rag", "enterprise", "contracts", "case-law", "bicep"],
            Stars: 1100,
            Author: "Azure"),

        new AgentSample(
            Id: "copilot-studio-samples-legal",
            Name: "Copilot Studio � Starter Agents",
            Description: "Official collection of Copilot Studio templates (including document Q&A and HR/policy copilots) usable as a base for legal and compliance agents. Manual import from the portal.",
            Category: AgentCategory.Legal,
            Target: DeploymentTarget.CopilotStudio,
            GitHubUrl: "https://github.com/microsoft/CopilotStudioSamples",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: CopilotStudioPortal,
            Tags: ["copilot-studio", "templates", "policy", "compliance"],
            Stars: 450,
            Author: "microsoft"),

        // ---------------- FINANCIAL ----------------
        new AgentSample(
            Id: "autogen-financial",
            Name: "AutoGen Multi-Agent (Financial Analyst demo)",
            Description: "Microsoft Research AutoGen framework. The `agentchat_stock_analysis` notebook showcases a team of agents (analyst + coder) producing financial analyses. Runs locally and deploys to Foundry.",
            Category: AgentCategory.Financial,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/autogen",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["autogen", "multi-agent", "python", "research"],
            Stars: 33000,
            Author: "microsoft"),

        new AgentSample(
            Id: "agent-openai-python-prompty",
            Name: "Agent OpenAI Python (Prompty)",
            Description: "Official template to build an Azure OpenAI agent with Prompty + Azure AI Foundry (Projects). Adaptable to financial use cases, KYC, investment memos.",
            Category: AgentCategory.Financial,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/agent-openai-python-prompty",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["prompty", "foundry", "python", "azd"],
            Stars: 220,
            Author: "Azure-Samples"),

        // ---------------- CUSTOMER SERVICE ----------------
        new AgentSample(
            Id: "contoso-chat",
            Name: "Contoso Chat � Retail Customer Service",
            Description: "Microsoft end-to-end reference for a customer service agent with RAG over product catalog + order history. Deploy with `azd up` on Azure AI Foundry.",
            Category: AgentCategory.CustomerService,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/contoso-chat",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["customer-service", "rag", "foundry", "prompty", "azd"],
            Stars: 500,
            Author: "Azure-Samples"),

        // ---------------- HR / PRODUCTIVITY ----------------
        new AgentSample(
            Id: "copilot-studio-samples-hr",
            Name: "Copilot Studio � HR / Policy Samples",
            Description: "Copilot Studio agent templates for HR: onboarding, policy Q&A, leave requests. Includes SharePoint/Dataverse connectors. Manual solution import.",
            Category: AgentCategory.HumanResources,
            Target: DeploymentTarget.CopilotStudio,
            GitHubUrl: "https://github.com/microsoft/CopilotStudioSamples",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: CopilotStudioPortal,
            Tags: ["hr", "onboarding", "sharepoint", "dataverse"],
            Stars: 450,
            Author: "microsoft"),

        // ---------------- EDUCATION ----------------
        new AgentSample(
            Id: "ai-agents-for-beginners",
            Name: "AI Agents for Beginners",
            Description: "Microsoft open-source course (10 lessons) that builds AI agents with Semantic Kernel, AutoGen and Azure AI Agent Service. Great starting point for educational tutors.",
            Category: AgentCategory.Education,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/ai-agents-for-beginners",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["education", "tutorial", "semantic-kernel", "autogen"],
            Stars: 20000,
            Author: "microsoft"),

        new AgentSample(
            Id: "aisearch-openai-rag-audio",
            Name: "Voice RAG Agent (audio tutor)",
            Description: "Voice agent built with Azure AI Search + GPT-4o realtime. Useful as a conversational tutor or voice help-desk. Deploy with azd.",
            Category: AgentCategory.Education,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/aisearch-openai-rag-audio",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["voice", "realtime", "rag", "gpt-4o"],
            Stars: 700,
            Author: "Azure-Samples"),

        // ---------------- GOVERNMENT / PUBLIC SECTOR ----------------
        new AgentSample(
            Id: "get-started-with-ai-agents",
            Name: "Get Started with AI Agents (Foundry)",
            Description: "Official starter template for the Azure AI Agent service in Foundry. Ideal base for public-sector agents: permit assistants, multilingual citizen Q&A.",
            Category: AgentCategory.Government,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/get-started-with-ai-agents",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["foundry", "agent-service", "starter", "azd"],
            Stars: 300,
            Author: "Azure-Samples"),

        // ---------------- PRODUCTIVITY ----------------
        new AgentSample(
            Id: "teams-ai",
            Name: "Teams AI Library",
            Description: "Microsoft official library to build agents inside Microsoft Teams (meeting summarizer, action items, assistants). Pluggable with Azure OpenAI and Foundry.",
            Category: AgentCategory.Productivity,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/teams-ai",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["teams", "graph", "summarization", "bot-framework"],
            Stars: 1400,
            Author: "microsoft"),

        new AgentSample(
            Id: "semantic-kernel",
            Name: "Semantic Kernel (Agents Framework)",
            Description: "Microsoft SDK to orchestrate agents, plugins and memory. C#, Python, Java. Foundation for any enterprise agent scenario on Azure AI Foundry.",
            Category: AgentCategory.Productivity,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/semantic-kernel",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["sdk", "orchestration", "plugins", "dotnet", "python", "azure-ai-foundry"],
            Stars: 22000,
            Author: "microsoft"),

        // =========================================================================
        //  MICROSOFT AGENT FRAMEWORK - the new unified Microsoft framework
        //  (convergence of Semantic Kernel + AutoGen) for building production-grade
        //  agents on Azure AI Foundry.
        // =========================================================================

        new AgentSample(
            Id: "microsoft-agent-framework",
            Name: "Microsoft Agent Framework",
            Description: "Official Microsoft framework to build enterprise AI agents (C# / Python). Unifies Semantic Kernel and AutoGen with native support for Azure AI Foundry Agent Service and multi-agent scenarios.",
            Category: AgentCategory.Productivity,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/agent-framework",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["microsoft-agent-framework", "azure-ai-foundry", "multi-agent", "dotnet", "python"],
            Stars: 1200,
            Author: "microsoft"),

        new AgentSample(
            Id: "ms-365-agents-sdk",
            Name: "Microsoft 365 Agents SDK",
            Description: "Official SDK to build multi-channel agents (Teams, Copilot Chat, web) deployable on Azure AI Foundry and publishable as agents in Microsoft 365 Copilot.",
            Category: AgentCategory.Productivity,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/microsoft/Agents",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["microsoft-365", "azure-ai-foundry", "teams", "multi-channel"],
            Stars: 400,
            Author: "microsoft"),

        new AgentSample(
            Id: "azureai-samples-foundry",
            Name: "Azure AI Samples � Foundry Agent Service",
            Description: "Official Azure AI samples repository: includes quickstarts and notebooks for the Foundry Agent Service, function calling, file-search, code interpreter.",
            Category: AgentCategory.Productivity,
            Target: DeploymentTarget.AzureAIFoundry,
            GitHubUrl: "https://github.com/Azure-Samples/azureai-samples",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: ["azure-ai-foundry", "foundry-agent-service", "quickstart", "samples"],
            Stars: 700,
            Author: "Azure-Samples"),
    ];

    public IReadOnlyList<AgentSample> GetAll() => _samples;

    public IEnumerable<AgentSample> Query(
        string? search = null,
        AgentCategory? category = null,
        DeploymentTarget? target = null)
    {
        IEnumerable<AgentSample> q = _samples;

        if (category is not null)
            q = q.Where(a => a.Category == category);

        if (target is not null)
            q = q.Where(a => a.Target == target || a.Target == DeploymentTarget.Both);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(a =>
                a.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                a.Tags.Any(t => t.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }

        return q.OrderByDescending(a => a.Stars);
    }

    // -------------------------------------------------------------------------
    //  SCAN: queries the GitHub Search API to discover new relevant repositories
    //  (AI agents for Azure AI Foundry / Copilot Studio / Microsoft Agent
    //  Framework) and adds them to the in-memory catalog if not already present.
    //
    //  Uses unauthenticated requests (rate limit 60/h per IP). For production
    //  register a GitHub token and add an Authorization header.
    // -------------------------------------------------------------------------

    // Targeted queries: ONLY agent-development projects based on
    //  - Azure AI Foundry (Agent Service, Foundry Projects)
    //  - Microsoft Copilot Studio
    //  - Microsoft Agent Framework
    //  - Foundry IQ (Knowledge on Azure AI Foundry)
    //
    // Queries combine topic/keyword to maximize relevant repositories and
    // filter by stars to reduce noise.
    private static readonly string[] SearchQueries =
    [
        "\"azure ai foundry\" agent in:name,description,readme stars:>10",
        "topic:azure-ai-foundry stars:>5",
        "\"foundry agent service\" in:name,description,readme stars:>5",
        "\"copilot studio\" in:name,description,readme stars:>10",
        "topic:copilot-studio stars:>5",
        "\"microsoft agent framework\" in:name,description,readme stars:>5",
        "topic:microsoft-agent-framework stars:>1",
        "\"foundry iq\" in:name,description,readme"
    ];

    // Accepted keywords: a repo is added to the catalog only if at least one
    // of these appears in the name, description or topics.
    private static readonly string[] AllowedKeywords =
    [
        "azure ai foundry", "azure-ai-foundry", "ai-foundry", "aifoundry",
        "foundry agent", "foundry-agent", "foundry iq", "foundry-iq",
        "copilot studio", "copilot-studio", "copilotstudio",
        "microsoft agent framework", "microsoft-agent-framework", "agent-framework"
    ];

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        if (_httpFactory is null)
            return new ScanResult(0, 0, "HttpClientFactory not available.");

        var http = _httpFactory.CreateClient("github");
        var existingUrls = new HashSet<string>(
            _samples.Select(s => s.GitHubUrl),
            StringComparer.OrdinalIgnoreCase);

        int inspected = 0, added = 0;
        string? errorMessage = null;

        foreach (var q in SearchQueries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var url = $"search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page=10";
                var resp = await http.GetFromJsonAsync<GitHubSearchResponse>(url, ct);
                if (resp?.Items is null) continue;

                foreach (var repo in resp.Items)
                {
                    inspected++;
                    if (repo.HtmlUrl is null || existingUrls.Contains(repo.HtmlUrl)) continue;
                    if (!IsRelevant(repo)) continue;

                    var sample = MapToSample(repo);
                    _samples.Add(sample);
                    existingUrls.Add(repo.HtmlUrl);
                    added++;
                }
            }
            catch (HttpRequestException ex)
            {
                errorMessage = $"HTTP error (rate-limit?): {ex.Message}";
                break;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error: {ex.Message}";
                break;
            }
        }

        // Persist discoveries so the next process start begins with the
        // full accumulated catalog. Only new additions trigger the write
        // (avoid needless disk I/O when the scan yields nothing).
        if (added > 0) SaveDiscovered();

        return new ScanResult(inspected, added, errorMessage);
    }

    /// <summary>
    /// Loads previously discovered agents from the JSON store and merges
    /// them into <see cref="_samples"/>, skipping any whose URL already
    /// exists in the seed. Best-effort: corrupt / missing files are ignored.
    /// </summary>
    private void LoadDiscovered()
    {
        try
        {
            if (!File.Exists(_discoveredStorePath)) return;
            string json;
            lock (_persistenceLock) json = File.ReadAllText(_discoveredStorePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var discovered = JsonSerializer.Deserialize<List<AgentSample>>(json, DiscoveredJsonOpts);
            if (discovered is null) return;

            var seen = new HashSet<string>(
                _samples.Select(s => s.GitHubUrl),
                StringComparer.OrdinalIgnoreCase);
            foreach (var s in discovered)
            {
                if (string.IsNullOrWhiteSpace(s.GitHubUrl)) continue;
                if (!seen.Add(s.GitHubUrl)) continue;
                _samples.Add(s);
            }
        }
        catch
        {
            // Best-effort: a corrupt store must never prevent the app from
            // starting. The next scan will simply rebuild it.
        }
    }

    /// <summary>
    /// Writes only the agents discovered via Scan (not the hardcoded seed)
    /// to the JSON store. Runs synchronously under a lock to protect
    /// concurrent scans, and swallows I/O errors because losing the
    /// discovered list is recoverable (another scan regenerates it).
    /// </summary>
    private void SaveDiscovered()
    {
        try
        {
            var toPersist = _samples
                .Where(s => !_seedIds.Contains(s.Id))
                .ToList();

            lock (_persistenceLock)
            {
                var dir = Path.GetDirectoryName(_discoveredStorePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(toPersist, DiscoveredJsonOpts);
                File.WriteAllText(_discoveredStorePath, json);
            }
        }
        catch
        {
            // Persistence is a nice-to-have; the in-memory catalog still
            // works for the current session if the disk write fails.
        }
    }

    private static bool IsRelevant(GitHubRepo r)
    {
        var haystack = ($"{r.Name} {r.FullName} {r.Description} {string.Join(' ', r.Topics ?? [])}")
            .ToLowerInvariant();
        return AllowedKeywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static AgentSample MapToSample(GitHubRepo r)
    {
        var text = $"{r.Name} {r.Description} {string.Join(' ', r.Topics ?? [])}".ToLowerInvariant();

        AgentCategory cat =
              text.Contains("health") || text.Contains("medical") || text.Contains("clinic") ? AgentCategory.Medical
            : text.Contains("legal") || text.Contains("contract") || text.Contains("compliance") ? AgentCategory.Legal
            : text.Contains("finance") || text.Contains("financial") || text.Contains("invest")  ? AgentCategory.Financial
            : text.Contains("retail") || text.Contains("shop") || text.Contains("ecommerce")     ? AgentCategory.Retail
            : text.Contains("hr") || text.Contains("onboard") || text.Contains("human-resources")? AgentCategory.HumanResources
            : text.Contains("edu") || text.Contains("tutor") || text.Contains("learning")        ? AgentCategory.Education
            : text.Contains("manufactur") || text.Contains("factory") || text.Contains("iot")    ? AgentCategory.Manufacturing
            : text.Contains("government") || text.Contains("public-sector") || text.Contains("citizen") ? AgentCategory.Government
            : text.Contains("support") || text.Contains("customer") || text.Contains("helpdesk") ? AgentCategory.CustomerService
            : AgentCategory.Productivity;

        bool hasCopilotStudio = text.Contains("copilot studio") || text.Contains("copilot-studio");
        bool hasFoundry = text.Contains("foundry") || text.Contains("ai-foundry");
        bool hasAgentFramework = text.Contains("agent framework") || text.Contains("agent-framework");
        bool hasFoundryIQ = text.Contains("foundry iq") || text.Contains("foundry-iq");

        DeploymentTarget target =
              hasCopilotStudio && hasFoundry ? DeploymentTarget.Both
            : hasCopilotStudio               ? DeploymentTarget.CopilotStudio
            :                                  DeploymentTarget.AzureAIFoundry;

        // Distinctive tags for the detected Microsoft technology
        var techTags = new List<string>();
        if (hasFoundry)         techTags.Add("azure-ai-foundry");
        if (hasCopilotStudio)   techTags.Add("copilot-studio");
        if (hasAgentFramework)  techTags.Add("microsoft-agent-framework");
        if (hasFoundryIQ)       techTags.Add("foundry-iq");

        var allTags = techTags
            .Concat(r.Topics ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new AgentSample(
            Id: r.FullName?.Replace('/', '-').ToLowerInvariant() ?? Guid.NewGuid().ToString("N"),
            Name: r.Name ?? "(unnamed)",
            Description: (r.Description ?? "Repository discovered via GitHub scan.").Trim(),
            Category: cat,
            Target: target,
            GitHubUrl: r.HtmlUrl ?? "",
            DeployToAzureUrl: null,
            CopilotStudioImportUrl: null,
            Tags: allTags,
            Stars: r.StargazersCount,
            Author: r.Owner?.Login ?? "unknown");
    }

    // DTO per la GitHub Search API
    private sealed record GitHubSearchResponse(
        [property: JsonPropertyName("items")] List<GitHubRepo>? Items);

    private sealed record GitHubRepo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("stargazers_count")] int StargazersCount,
        [property: JsonPropertyName("topics")] List<string>? Topics,
        [property: JsonPropertyName("owner")] GitHubOwner? Owner);

    private sealed record GitHubOwner(
        [property: JsonPropertyName("login")] string? Login);
}

public sealed record ScanResult(int Inspected, int Added, string? Error);
