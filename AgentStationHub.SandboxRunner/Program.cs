using System.Text.Json;
using AgentStationHub.SandboxRunner.Contracts;
using AgentStationHub.SandboxRunner.Team;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;

// Agent Framework sandbox runner. Reads a JSON RunnerRequest from stdin,
// dispatches to the requested command, streams intermediate agent trace
// events to stderr (so the host can forward them to the Live log) and
// writes the final RunnerResponse JSON to stdout.
//
// Required environment variables:
//   AZURE_OPENAI_ENDPOINT   - Azure OpenAI endpoint
//   AZURE_OPENAI_DEPLOYMENT - chat-capable model deployment (e.g. gpt-5.3-chat)
//   AZURE_OPENAI_API_KEY    - optional; if missing, uses DefaultAzureCredential
//   AZURE_TENANT_ID         - optional; pins the AAD tenant for DefaultAzureCredential
//
// The main app (AgentStationHub) continues to use the Responses API directly
// for its monolithic VerifierAgent/PlanExtractorAgent. This runner uses the
// Chat Completions API because Microsoft.Agents.AI 1.1.0 + Azure.AI.OpenAI
// 2.9.x do not yet expose a Responses client via AzureOpenAIClient.

var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

try
{
    var requestJson = await Console.In.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<RunnerRequest>(requestJson)
        ?? throw new InvalidOperationException("Empty request payload on stdin.");

    // Pick the deployment best suited to the agent role. Reasoning model
    // (o4-mini) for the Doctor, full GPT-5.x for the Strategist, fast
    // mini for the Verifier. Each is overridable via env var; all fall
    // back to AZURE_OPENAI_DEPLOYMENT for single-model deployments.
    var deploymentForRole = PickDeploymentForCommand(request.Command);
    var chatClient = BuildChatClient(deploymentForRole);

    var trace = new List<AgentTraceDto>();
    void OnTrace(AgentTraceDto t)
    {
        trace.Add(t);
        Console.Error.WriteLine($"[agent] {t.Agent}/{t.Stage}: {t.Message}");
    }

    RunnerResponse response = request.Command switch
    {
        "plan"      => await RunPlanAsync(chatClient, request, OnTrace, default),
        "verify"    => await RunVerifyAsync(chatClient, request, OnTrace, default),
        "remediate" => await RunRemediateAsync(chatClient, request, OnTrace, default),
        _ => new RunnerResponse(false, $"Unknown command '{request.Command}'", null, null, trace, null)
    };

    var final = response with { Trace = trace };
    Console.Out.WriteLine(JsonSerializer.Serialize(final, jsonOpts));
    return final.Ok ? 0 : 1;
}
catch (Exception ex)
{
    var err = new RunnerResponse(false, $"{ex.GetType().Name}: {ex.Message}", null, null, null, null);
    Console.Out.WriteLine(JsonSerializer.Serialize(err, jsonOpts));
    Console.Error.WriteLine(ex.ToString());
    return 2;
}

static ChatClient BuildChatClient(string deployment)
{
    var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not set");
    if (string.IsNullOrWhiteSpace(deployment))
        throw new InvalidOperationException(
            "No deployment resolved for the current command. Set AZURE_OPENAI_DEPLOYMENT " +
            "or one of AZURE_OPENAI_DEPLOYMENT_{STRATEGIST,DOCTOR,VERIFIER}.");
    var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
    var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
    AzureOpenAIClient azureClient;
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }
    else
    {
        var credOpts = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            credOpts.TenantId = tenantId;
            credOpts.VisualStudioTenantId = tenantId;
            credOpts.SharedTokenCacheTenantId = tenantId;
            credOpts.InteractiveBrowserTenantId = tenantId;
        }
        azureClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential(credOpts));
    }
    Console.Error.WriteLine($"[runner] using deployment: {deployment}");
    return azureClient.GetChatClient(deployment);
}

// Resolve the Azure OpenAI deployment to use for a given runner command.
// The orchestrator forwards distinct deployments per agent role:
//   - plan       => Strategist (planning, structured extraction)
//   - remediate  => Doctor     (reasoning under failure)
//   - verify     => Verifier   (small/fast endpoint check)
// Each role-specific env var falls back to AZURE_OPENAI_DEPLOYMENT so a
// single-model deployment continues to work without changes.
static string PickDeploymentForCommand(string command)
{
    var fallback = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "";
    string? specific = command switch
    {
        "plan"      => Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_STRATEGIST"),
        "remediate" => Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_DOCTOR"),
        "verify"    => Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_VERIFIER"),
        _           => null
    };
    return string.IsNullOrWhiteSpace(specific) ? fallback : specific!;
}

static async Task<RunnerResponse> RunPlanAsync(
    ChatClient chatClient, RunnerRequest request,
    Action<AgentTraceDto> trace, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(request.RepoUrl))
        return new RunnerResponse(false, "plan: repoUrl is required", null, null, null, null);
    if (string.IsNullOrWhiteSpace(request.Workspace))
        return new RunnerResponse(false, "plan: workspace is required", null, null, null, null);

    var team = new PlanningTeam(chatClient, trace);
    var plan = await team.RunAsync(request.RepoUrl!, request.Workspace, request.AzureLocation, ct,
        request.PriorInsights ?? Array.Empty<PriorInsightDto>());
    return new RunnerResponse(true, null, plan, null, null, null);
}

static async Task<RunnerResponse> RunVerifyAsync(
    ChatClient chatClient, RunnerRequest request,
    Action<AgentTraceDto> trace, CancellationToken ct)
{
    // Placeholder: keep the monolithic VerifierAgent in the host for this
    // iteration; migrate it to a dedicated agent here as a follow-up PR.
    await Task.CompletedTask;
    trace(new AgentTraceDto("Verifier", "skipped", "Verifier stays in host for this iteration."));
    return new RunnerResponse(false, "verify not implemented in this runner version", null, null, null, null);
}

static async Task<RunnerResponse> RunRemediateAsync(
    ChatClient chatClient, RunnerRequest request,
    Action<AgentTraceDto> trace, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(request.Workspace))
        return new RunnerResponse(false, "remediate: workspace is required", null, null, null, null);
    if (request.Plan is null)
        return new RunnerResponse(false, "remediate: plan is required", null, null, null, null);
    if (request.FailedStepId is null)
        return new RunnerResponse(false, "remediate: failedStepId is required", null, null, null, null);

    var team = new PlanningTeam(chatClient, trace);
    var remediation = await team.RemediateAsync(
        request.Workspace,
        request.Plan,
        request.FailedStepId.Value,
        request.ErrorTail ?? "",
        request.PreviousAttempts ?? Array.Empty<string>(),
        ct,
        request.PriorInsights ?? Array.Empty<PriorInsightDto>());
    return new RunnerResponse(true, null, null, null, null, remediation);
}




