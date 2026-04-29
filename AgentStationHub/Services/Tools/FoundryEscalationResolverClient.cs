using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStationHub.Models;
using AgentStationHub.Services.Security;
using Azure.Core;
using Azure.Identity;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Foundry-hosted variant of <see cref="AgentStationHub.Services.Agents.EscalationResolverAgent"/>.
///
/// Uses the SAME Foundry Responses API pattern as <see cref="FoundryAgentChatClient"/>
/// (Agent Learn) — instructions, model choice and tool routing live in the
/// Foundry portal under an agent named e.g. <c>AgentEscalationResolver</c>;
/// this client is just a transport. POST
/// <c>{ProjectEndpoint}/openai/v1/responses</c> with
/// <c>agent_reference={type:"agent_reference", name:&lt;AgentName&gt;}</c>
/// and a single user input carrying the failing-step JSON envelope.
///
/// Activated by the feature flag <c>Foundry:UseFoundryEscalationResolver=true</c>;
/// when off (or when this client is not registered) the orchestrator falls
/// back to the in-process <see cref="AgentStationHub.Services.Agents.EscalationResolverAgent"/>.
/// </summary>
public sealed class FoundryEscalationResolverClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _projectEndpoint;
    private readonly string _agentName;
    private readonly string? _tenantId;
    private readonly ILogger<FoundryEscalationResolverClient> _log;

    private TokenCredential? _credential;
    private AccessToken _cachedToken;

    public FoundryEscalationResolverClient(
        HttpClient http,
        string projectEndpoint,
        string agentName,
        string? tenantId,
        ILogger<FoundryEscalationResolverClient> log)
    {
        _http = http;
        _projectEndpoint = projectEndpoint.TrimEnd('/');
        _agentName = agentName;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _log = log;
    }

    public async Task<Remediation?> ResolveAsync(
        DeploymentStep failingStep,
        string failingCommand,
        string stepTail,
        string doctorReasoning,
        IReadOnlyList<string> previousAttempts,
        CancellationToken ct)
    {
        try
        {
            // Same envelope shape used by the in-process agent so the
            // hosted agent's instructions can be authored once and work
            // across both call sites.
            var payload = new
            {
                failingStep = new
                {
                    id = failingStep.Id,
                    description = failingStep.Description,
                    command = failingCommand,
                    workingDirectory = failingStep.WorkingDirectory,
                },
                errorTail = TruncateTail(stepTail, 6000),
                doctorReasoning = TruncateTail(doctorReasoning, 2000),
                previousAttempts = previousAttempts.Take(20).ToArray(),
            };
            var userJson = JsonSerializer.Serialize(payload);

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
                                ["text"] = userJson,
                            },
                        },
                    },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(JsonOpts), Encoding.UTF8, "application/json"),
            };
            var token = await GetTokenAsync(ct).ConfigureAwait(false);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Foundry EscalationResolver HTTP {Code}: {Body}",
                    (int)resp.StatusCode, Truncate(raw, 600));
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Foundry EscalationResolver run did not complete (status={Status}).", status);
                return null;
            }

            var assistantText = ExtractAssistantText(root);
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                _log.LogInformation("Foundry EscalationResolver returned empty assistant text.");
                return null;
            }

            var decision = ParseDecision(assistantText);
            if (decision is null)
            {
                _log.LogInformation(
                    "Foundry EscalationResolver output not parseable JSON: {Raw}",
                    Truncate(assistantText, 500));
                return null;
            }

            if (string.Equals(decision.Kind, "give_up", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation(
                    "Foundry EscalationResolver chose give_up: {Rationale}", decision.Rationale);
                return null;
            }

            var cmd = decision.Command?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(cmd))
            {
                _log.LogInformation("Foundry EscalationResolver returned empty command.");
                return null;
            }

            // Same de-dup heuristics as the in-process agent.
            if (string.Equals(Normalise(cmd), Normalise(failingCommand), StringComparison.Ordinal))
            {
                _log.LogInformation("Foundry EscalationResolver echoed the failing command — discarding.");
                return null;
            }
            foreach (var prev in previousAttempts)
            {
                if (!string.IsNullOrWhiteSpace(prev)
                    && prev.Contains(Truncate(cmd, 80), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Foundry EscalationResolver suggestion already attempted — discarding.");
                    return null;
                }
            }

            var kind = string.Equals(decision.Kind, "replace_step", StringComparison.OrdinalIgnoreCase)
                ? "replace_step"
                : "insert_before";

            var step = new DeploymentStep(
                Id: 0,
                Description:
                    $"[FoundryEscalationResolver] {decision.Rationale ?? "auto-resolved escalation"} " +
                    $"(confidence={decision.Confidence:0.00})",
                Command: cmd,
                WorkingDirectory: failingStep.WorkingDirectory ?? ".");

            var (ok, reason) = PlanValidator.Validate(step);
            if (!ok)
            {
                _log.LogWarning(
                    "Foundry EscalationResolver suggestion rejected by PlanValidator: {Reason}. Cmd: {Cmd}",
                    reason, Truncate(cmd, 200));
                return null;
            }

            return new Remediation(
                Kind: kind,
                StepId: failingStep.Id,
                NewSteps: new[] { step },
                Reasoning:
                    $"[foundry-escalation-resolver] {decision.Rationale} " +
                    $"(kind={kind}, confidence={decision.Confidence:0.00})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Foundry EscalationResolver call failed.");
            return null;
        }
    }

    // -- helpers (mirror the in-process agent) --

    private static ResolverDecision? ParseDecision(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }
        try
        {
            return JsonSerializer.Deserialize<ResolverDecision>(
                trimmed, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

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
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string TruncateTail(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[^max..]);

    private static string Normalise(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private sealed class ResolverDecision
    {
        public string? Kind { get; set; }
        public string? Command { get; set; }
        public string? Rationale { get; set; }
        public double Confidence { get; set; }
    }
}
