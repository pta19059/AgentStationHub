using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Thin client over the Microsoft Foundry **Agents v2 Responses API**.
///
/// Replaces the old AOAI-bypass implementation. Instead of re-implementing
/// the agent's tools (catalog Logic App + Microsoft Learn MCP), we delegate
/// the entire turn to the Foundry hosted agent <c>AgentMicrosoftLearn</c>:
/// the runtime owns instructions, tool routing (MCP + OpenAPI), and
/// grounding. Result ≡ what the user sees in the Foundry playground.
///
/// Endpoint pattern:
///   POST {ProjectEndpoint}/openai/v1/responses
///   Authorization: Bearer &lt;DefaultAzureCredential token, scope https://ai.azure.com/.default&gt;
///   Body: { "agent_reference": { "type":"agent_reference", "name":"&lt;AgentName&gt;" },
///           "input": [{ "role":"user", "content":[{ "type":"input_text", "text": ... }] }],
///           "previous_response_id": "&lt;optional, threads conversation&gt;" }
///
/// The returned <c>response.id</c> is reused as the next call's
/// <c>previous_response_id</c> so the agent has multi-turn memory inside
/// the same Blazor circuit.
/// </summary>
public sealed class FoundryAgentChatClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string? _projectEndpoint;
    private readonly string? _agentName;
    private readonly string? _tenantId;
    private readonly string _displayLabel;

    private TokenCredential? _credential;
    private AccessToken _cachedToken;

    public FoundryAgentChatClient(
        HttpClient http,
        string? projectEndpoint,
        string? agentName,
        string? tenantId,
        string? assistantIdLabel = null)
    {
        _http = http;
        _projectEndpoint = string.IsNullOrWhiteSpace(projectEndpoint)
            ? null
            : projectEndpoint!.TrimEnd('/');
        _agentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _displayLabel = string.IsNullOrWhiteSpace(assistantIdLabel)
            ? (_agentName ?? "Agent")
            : assistantIdLabel!;
    }

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_projectEndpoint) && !string.IsNullOrEmpty(_agentName);

    public string AssistantId => _displayLabel;

    /// <summary>
    /// Send one user turn to the Foundry agent. <paramref name="threadId"/>
    /// is the previous response id (or <c>null</c> for the first turn).
    /// Returns the new response id (use it as the next threadId) and the
    /// assistant's final text.
    /// </summary>
    public async Task<(string? threadId, string reply)> SendAsync(
        string? threadId,
        string userMessage,
        CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "FoundryAgentChatClient is not configured. Set Foundry:ChatAgent:ProjectEndpoint and Foundry:ChatAgent:AgentName.");

        var url = $"{_projectEndpoint}/openai/v1/responses";

        var body = new JsonObject
        {
            ["agent_reference"] = new JsonObject
            {
                ["type"] = "agent_reference",
                ["name"] = _agentName,
            },
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = userMessage,
                        },
                    },
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(threadId))
            body["previous_response_id"] = threadId;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(JsonOpts), Encoding.UTF8, "application/json"),
        };
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Azure content-safety guardrail: when the prompt trips the
            // "Prompt Shields" / Jailbreak / hate / sexual / violence /
            // self-harm filters the runtime returns 400 with code
            // "content_filter" before reaching the model. We mirror what
            // the playground does and turn it into a polite assistant
            // reply instead of a red error bubble — the threadId is
            // preserved so the user can simply rephrase.
            if ((int)resp.StatusCode == 400 && IsContentFilterError(raw, out var category))
            {
                var polite = string.IsNullOrEmpty(category)
                    ? "I can't help with that request — it was blocked by the content-safety policy. Please rephrase and try again."
                    : $"I can't help with that request — it was blocked by the content-safety policy ({category}). Please rephrase and try again.";
                return (threadId, polite);
            }

            // Surface other API errors verbatim — the panel shows it as a chat
            // bubble, which makes diagnosing portal-side mis-config (missing
            // role assignment, wrong agent name, bloated tool response, …)
            // a one-click affair without trawling container logs.
            throw new InvalidOperationException(
                $"Foundry agent call failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(raw, 1200)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var newId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : threadId;

        var status = root.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            // Pull the runtime's structured error if present (e.g. tool size cap).
            var errMsg = root.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.Object
                && errEl.TryGetProperty("message", out var emEl)
                    ? emEl.GetString()
                    : null;
            throw new InvalidOperationException(
                $"Foundry agent run did not complete (status={status ?? "<null>"}): {errMsg ?? Truncate(raw, 800)}");
        }

        var reply = ExtractAssistantText(root);
        return (newId, reply);
    }

    /// <summary>
    /// Walk the <c>output</c> array and concatenate the text of every
    /// <c>message</c> item produced by the assistant. Other item types
    /// (<c>mcp_list_tools</c>, <c>mcp_call</c>, <c>function_call</c>, …)
    /// are diagnostic and intentionally ignored — we only want the final
    /// natural-language reply.
    /// </summary>
    private static string ExtractAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outEl) || outEl.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in outEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var tEl) || tEl.GetString() != "message") continue;
            if (!item.TryGetProperty("content", out var cEl) || cEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var block in cEl.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var btEl)) continue;
                var btype = btEl.GetString();
                // Foundry uses `output_text` for the final answer, but we
                // also accept the older `text` shape for forward-compat.
                if (btype == "output_text" || btype == "text")
                {
                    if (block.TryGetProperty("text", out var txEl) && txEl.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(txEl.GetString());
                    }
                }
            }
        }
        return sb.ToString();
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        // Cheap in-memory cache: the Responses API accepts standard AAD
        // bearer tokens with ~60min lifetime. Refresh 2 minutes before expiry.
        if (_cachedToken.Token is { Length: > 0 } &&
            _cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return _cachedToken.Token;
        }

        _credential ??= BuildCredential();
        var ctx = new TokenRequestContext(new[] { "https://ai.azure.com/.default" });
        _cachedToken = await _credential.GetTokenAsync(ctx, ct).ConfigureAwait(false);
        return _cachedToken.Token;
    }

    private TokenCredential BuildCredential()
    {
        // DefaultAzureCredential picks up env vars (AZURE_CLIENT_ID/SECRET/TENANT_ID)
        // in the container and falls back to managed identity / az login locally.
        // We pin the tenant when AzureOpenAI:TenantId is configured to avoid
        // multi-tenant guest-account ambiguity.
        if (!string.IsNullOrWhiteSpace(_tenantId))
        {
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = _tenantId,
                AdditionallyAllowedTenants = { "*" },
            });
        }
        return new DefaultAzureCredential();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Recognise an Azure OpenAI content-safety rejection. The 400 body
    /// looks like:
    ///   { "error": { "code":"content_filter", "param":"prompt",
    ///                "message":"…", "content_filters":[{"category":"sexual",…}] } }
    /// We extract the first triggered category for a slightly more
    /// informative user-facing message.
    /// </summary>
    private static bool IsContentFilterError(string raw, out string category)
    {
        category = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("error", out var err) ||
                err.ValueKind != JsonValueKind.Object) return false;

            var code = err.TryGetProperty("code", out var cEl) ? cEl.GetString() : null;
            var msg = err.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
            var triggered =
                string.Equals(code, "content_filter", StringComparison.OrdinalIgnoreCase) ||
                (msg is not null &&
                 msg.IndexOf("content management policy", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!triggered) return false;

            if (err.TryGetProperty("content_filters", out var cf) && cf.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in cf.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    var filtered = f.TryGetProperty("filtered", out var fEl) && fEl.ValueKind == JsonValueKind.True;
                    if (!filtered) continue;
                    if (f.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.String)
                    {
                        category = catEl.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
