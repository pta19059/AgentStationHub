// Hosted Doctor agent � Foundry runtime entry point.
//
// This is the ash-doctor agent extracted from the in-sandbox SandboxRunner
// and packaged as a standalone Foundry Hosted Agent. Architectural rationale
// in the parent repo's commit history; in short: the Doctor benefits the most
// from Foundry's eval / prompt-optimizer surface, has the smallest call-site
// surface to swap (single host method), and degrades gracefully when offline
// (the sandbox runner remains as a fallback path behind a feature flag).
//
// Wire diagram:
//   AgentStationHub host (Blazor)
//      �  HTTP POST /invocations
//      �   (when AzureOpenAI:UseFoundryDoctor=true)
//   Foundry Hosted Agent runtime (this process)
//      �  AgentHost + Invocations SDK
//      �  DoctorInvocationHandler.HandleAsync
//      �   parses RunnerRequest, calls DoctorBrain
//   DoctorBrain
//      �  Azure.AI.OpenAI ChatClient against ash-doctor (o4-mini)
//      �  emits RemediationDto JSON
//   ?  back through the wire to the host orchestrator.

using Azure.AI.AgentServer.Invocations;
using AgentStationHub.DoctorAgent;
using Microsoft.Agents.AI;

var builder = AgentHost.CreateBuilder(args);

builder.Services.AddSingleton<DoctorBrain>();

builder.Services.AddInvocationsServer();
builder.Services.AddScoped<InvocationHandler, DoctorInvocationHandler>();

builder.RegisterProtocol("invocations",
    endpoints => endpoints.MapInvocationsServer());

var app = builder.Build();
app.Run();
