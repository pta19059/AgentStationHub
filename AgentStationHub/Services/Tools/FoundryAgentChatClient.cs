using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Foundry-Agent-equivalent chat client backed directly by Azure OpenAI
/// Chat Completions + function calling. We bypass the Foundry V2 Responses
/// runtime (still preview, returns 404 on this cluster) and re-implement the
/// "Agent Learn" agent ourselves: hard-coded system instructions, single
/// function tool that proxies the Microsoft Learn catalog through a Logic
/// App. Conversation history lives in this client (per-circuit) so the chat
/// panel keeps memory across turns.
///
/// URL shape:
///   {Endpoint}/openai/deployments/{Deployment}/chat/completions?api-version=...
/// Auth:
///   Bearer token from <see cref="DefaultAzureCredential"/> against
///   <c>https://cognitiveservices.azure.com/.default</c>. The Hub principal
///   needs <c>Cognitive Services OpenAI User</c> on the AOAI account.
/// </summary>
public sealed class FoundryAgentChatClient
{
    private static readonly string[] AoaiScope =
        new[] { "https://cognitiveservices.azure.com/.default" };

    private const string ApiVersion = "2024-10-21";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // System prompt = the same Agent Learn instructions configured in
    // Foundry, adapted so the model knows about the local function tool.
    private const string SystemPrompt = """
You are Agent Learn, a friendly Microsoft Learn study advisor. You help users
discover the right official training material — modules, learning paths,
applied skills, certifications, exams, and instructor-led courses — from the
Microsoft Learn catalog.

Tool:
- get_learning_content() returns the full Microsoft Learn catalog as JSON
  (arrays: modules, learningPaths, appliedSkills, certifications, exams,
  courses; each item has title, summary, url, levels, roles, products,
  subjects, type, durationInMinutes).
- Call it the FIRST time the user asks anything related to learning,
  training, courses, certifications, exams, study plans, or "how do I learn
  X". Re-use the result for the rest of the conversation; only call again
  if the user explicitly asks to refresh.

How to answer (natural language with citations):
1. Never show raw JSON, code blocks of objects, field names, or schema.
2. Reply in fluent natural language, mirroring the user's language
   (Italian if they wrote in Italian, English otherwise). Be warm and
   conversational, like a knowledgeable colleague.
3. Filter the catalog locally by intent (role, product, subject, level,
   keywords in title/summary). Pick 3 to 6 of the most relevant items —
   favor a clear progression (one path + a couple of modules + one
   certification when applicable).
4. Weave each suggestion into prose, not a dry list. Each recommendation
   MUST be cited with a Markdown link directly on the title, e.g.
   "Ti consiglio di partire da [Introduzione ad Azure AI](https://learn...),
   un modulo introduttivo di circa 45 minuti…".
5. For each item mention briefly: why it fits, the type (module / learning
   path / certification / exam), the level, and the estimated time when
   available — woven into the prose, never in a tag list.
6. Close with a short, encouraging next step.

Style:
- Concise but human. No bullet-point dumps unless the user asks for a list.
- Never mention the tool, API, Logic App, JSON, or "I queried the catalog".
- Always cite real URLs taken from the tool response — never invent links.
- If nothing matches, say so honestly and suggest the closest topic.
- If the tool fails, apologize briefly and invite the user to retry.

Out of scope:
- You don't write code, deploy resources, or troubleshoot Azure issues.
  Steer the user back to relevant Microsoft Learn material.
- You only recommend content from the Microsoft Learn catalog.
""";

    private readonly HttpClient _http;
    private readonly TokenCredential _cred;
    private readonly string _aoaiUrl;
    private readonly string _learnToolUrl;

    /// <summary>
    /// Per-circuit conversation history. The Razor panel re-uses the same
    /// client singleton, but each panel keeps a thread id; we map that id
    /// to a list of messages stored here. (Kept simple — fine for a demo.)
    /// </summary>
    private readonly Dictionary<string, List<ChatMessage>> _threads = new();

    public bool IsConfigured { get; }

    /// <summary>Reported in the panel header. Cosmetic only.</summary>
    public string AssistantId { get; }

    public FoundryAgentChatClient(
        HttpClient http,
        string? openAiEndpoint,
        string? deployment,
        string? learnToolUrl,
        string? tenantId,
        string? assistantIdLabel = null)
    {
        _http = http;
        var endpoint = (openAiEndpoint ?? string.Empty).TrimEnd('/');
        var dep = (deployment ?? string.Empty).Trim();
        _learnToolUrl = (learnToolUrl ?? string.Empty).Trim();
        AssistantId = string.IsNullOrWhiteSpace(assistantIdLabel) ? dep : assistantIdLabel;

        IsConfigured = !string.IsNullOrWhiteSpace(endpoint)
                    && !string.IsNullOrWhiteSpace(dep)
                    && !string.IsNullOrWhiteSpace(_learnToolUrl);

        _aoaiUrl = IsConfigured
            ? $"{endpoint}/openai/deployments/{dep}/chat/completions?api-version={ApiVersion}"
            : string.Empty;

        _cred = string.IsNullOrWhiteSpace(tenantId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
    }

    /// <summary>
    /// Sends a user turn. Returns (threadId, assistantText). On the first
    /// call pass <paramref name="threadId"/> = null; reuse the returned id
    /// to keep history. Tool calls (Microsoft Learn lookup) are resolved
    /// internally, the caller only ever sees the final assistant text.
    /// </summary>
    public async Task<(string ThreadId, string Reply)> SendAsync(
        string? threadId,
        string userMessage,
        CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "FoundryAgentChatClient is not configured (OpenAI endpoint / deployment / Learn tool URL).");

        threadId ??= Guid.NewGuid().ToString("N");
        if (!_threads.TryGetValue(threadId, out var history))
        {
            history = new List<ChatMessage>
            {
                new("system", SystemPrompt),
            };
            _threads[threadId] = history;
        }
        history.Add(new ChatMessage("user", userMessage));

        var token = await _cred.GetTokenAsync(new TokenRequestContext(AoaiScope), ct);

        // Tool definition. The function returns the Microsoft Learn catalog
        // JSON verbatim — we declare it with no parameters so the model
        // can't pass anything we'd then have to validate. The Logic App
        // already knows what to do.
        var tools = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "get_learning_content",
                    description = "Returns the full Microsoft Learn catalog (modules, learningPaths, certifications, exams, courses) as JSON. Call once per conversation; results are cached.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false,
                    },
                },
            },
        };

        // Up to 3 tool-call iterations. In practice the model calls
        // get_learning_content at most once per turn.
        for (var iter = 0; iter < 3; iter++)
        {
            var body = new
            {
                messages = history.Select(m => m.ToWire()).ToArray(),
                tools,
                tool_choice = "auto",
                temperature = 0.3,
                max_tokens = 1500,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _aoaiUrl)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"OpenAI {(int)resp.StatusCode}: {Truncate(raw, 600)}");

            using var doc = JsonDocument.Parse(raw);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            // Pull text content (may be null when the model only emits tool calls).
            string? content = null;
            if (msg.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                content = cEl.GetString();

            // Tool calls?
            if (msg.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array && tcEl.GetArrayLength() > 0)
            {
                // Persist the assistant turn so the next round refers to it.
                var toolCalls = tcEl.EnumerateArray()
                    .Select(tc => new ToolCall(
                        tc.GetProperty("id").GetString() ?? "",
                        tc.GetProperty("function").GetProperty("name").GetString() ?? ""))
                    .ToList();
                history.Add(new ChatMessage("assistant", content ?? "", toolCalls));

                foreach (var call in toolCalls)
                {
                    var toolResult = call.Name == "get_learning_content"
                        ? await CallLearnAsync(ct)
                        : "{\"error\":\"unknown_tool\"}";
                    history.Add(new ChatMessage("tool", toolResult)
                    {
                        ToolCallId = call.Id,
                    });
                }
                continue; // loop and ask the model to summarize the tool result
            }

            // Plain assistant message — done.
            var finalText = string.IsNullOrWhiteSpace(content) ? "(no reply)" : content!.Trim();
            history.Add(new ChatMessage("assistant", finalText));
            return (threadId, finalText);
        }

        return (threadId, "(tool loop exceeded)");
    }

    /// <summary>
    /// Calls the Logic App (anonymous SAS auth, the URL already embeds
    /// sig/sp/sv) and returns the catalog JSON as a string. We cap the
    /// payload sent to the model at ~120 KB to keep the prompt within
    /// gpt-4.1-mini's context budget (the raw catalog is ~14 MB).
    /// </summary>
    private async Task<string> CallLearnAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _learnToolUrl)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return $"{{\"error\":\"learn_tool_{(int)resp.StatusCode}\",\"body\":{JsonSerializer.Serialize(Truncate(raw, 200))}}}";

        // The catalog is huge. Trim to ~120 KB of JSON; the model can
        // still pick relevant items from the head of each array.
        return TruncateBytes(raw, 120 * 1024);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string TruncateBytes(string s, int maxBytes)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(s);
        if (bytes <= maxBytes) return s;
        // Cheap char-based cut — good enough.
        var keep = (int)((double)maxBytes / bytes * s.Length);
        return s[..keep] + "\n…(truncated)";
    }

    private sealed record ToolCall(string Id, string Name);

    private sealed class ChatMessage
    {
        public string Role { get; }
        public string Content { get; }
        public List<ToolCall>? ToolCalls { get; }
        public string? ToolCallId { get; init; }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public ChatMessage(string role, string content, List<ToolCall> toolCalls)
        {
            Role = role;
            Content = content;
            ToolCalls = toolCalls;
        }

        public object ToWire()
        {
            if (Role == "assistant" && ToolCalls is { Count: > 0 })
            {
                return new
                {
                    role = "assistant",
                    content = string.IsNullOrEmpty(Content) ? null : Content,
                    tool_calls = ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = "{}" },
                    }).ToArray(),
                };
            }
            if (Role == "tool")
            {
                return new
                {
                    role = "tool",
                    tool_call_id = ToolCallId,
                    content = Content,
                };
            }
            return new { role = Role, content = Content };
        }
    }
}
