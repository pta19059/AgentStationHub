using System.Text.Json;
using Azure.AI.AgentServer.Invocations;
using Microsoft.AspNetCore.Http;

namespace AgentStationHub.DoctorAgent;

/// <summary>
/// Bridges the Foundry Invocations protocol (raw HTTP body) and the
/// internal DoctorBrain. Reads JSON DoctorRequest from the body, runs the
/// brain, writes JSON DoctorResponse back. Errors are caught here and
/// turned into ok=false so the orchestrator can fall back to the local
/// sandbox-runner Doctor without hard-failing the deployment.
/// </summary>
public sealed class DoctorInvocationHandler(
    DoctorBrain brain,
    ILogger<DoctorInvocationHandler> log) : InvocationHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization
            .JsonIgnoreCondition.WhenWritingNull,
    };

    public override async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        DoctorResponse result;
        try
        {
            var req = await JsonSerializer.DeserializeAsync<DoctorRequest>(
                request.Body, JsonOpts, cancellationToken)
                ?? throw new InvalidOperationException("empty request body");

            if (!string.Equals(req.Command, "remediate", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"unsupported command '{req.Command}' (only 'remediate' is hosted on Foundry)");
            }

            log.LogInformation(
                "doctor invoke: workspace={Workspace} failedStep={Step} attempts={Attempts} files={Files}",
                req.Workspace, req.FailedStepId,
                req.PreviousAttempts?.Count ?? 0,
                req.RepoFiles?.Count ?? 0);

            var (remediation, trace) = await brain.RemediateAsync(req, cancellationToken);

            result = new DoctorResponse
            {
                Ok = true,
                Remediation = remediation,
                Trace = trace,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "doctor invocation failed");
            result = new DoctorResponse
            {
                Ok = false,
                Error = ex.Message,
            };
        }

        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, result, JsonOpts, cancellationToken);
    }
}
