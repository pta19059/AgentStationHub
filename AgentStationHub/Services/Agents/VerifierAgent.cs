using System.ClientModel;
using System.Text.Json;
using OpenAI.Responses;

namespace AgentStationHub.Services.Agents;

public sealed record VerificationResult(bool Success, string? Endpoint, string? Notes);

#pragma warning disable OPENAI001
public sealed class VerifierAgent
{
    private readonly OpenAIResponseClient _responses;

    public VerifierAgent(OpenAIResponseClient responses) => _responses = responses;

    public async Task<VerificationResult> VerifyAsync(
        string tailLogs,
        IEnumerable<string> hints,
        CancellationToken ct)
    {
        const string system = """
            You analyze deployment logs. Respond ONLY with JSON:
            { "success": bool, "endpoint": string|null, "notes": string }
            """;

        var items = new List<ResponseItem>
        {
            ResponseItem.CreateSystemMessageItem(system),
            ResponseItem.CreateUserMessageItem($"HINTS: {string.Join(", ", hints)}\nLOG TAIL:\n{tailLogs}")
        };

        var options = new ResponseCreationOptions
        {
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonObjectFormat()
            }
        };

        ClientResult<OpenAIResponse> result = await _responses.CreateResponseAsync(items, options, ct);
        var content = result.Value.GetOutputText() ?? "{}";
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        var json = (start >= 0 && end > start) ? content[start..(end + 1)] : "{}";

        using var doc = JsonDocument.Parse(json);
        return new VerificationResult(
            Success: doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean(),
            Endpoint: doc.RootElement.TryGetProperty("endpoint", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            Notes: doc.RootElement.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
    }
}
#pragma warning restore OPENAI001
