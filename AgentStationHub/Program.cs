using AgentStationHub.Components;
using AgentStationHub.Hubs;
using AgentStationHub.Services;
using AgentStationHub.Services.Agents;
using AgentStationHub.Services.Security;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using OpenAI.Responses;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

// The DataProtection key store lives on a persistent volume without any
// at-rest encryption (acceptable for a single-tenant local dev tool;
// the volume is root-only inside the container and the host bind mount
// sits under the user's profile). ASP.NET Core logs a warning for each
// new key that gets persisted without an encryptor � downgrade that one
// category to Error so it doesn't clutter the container logs while still
// surfacing real key-management failures.
builder.Logging.AddFilter(
    "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager",
    LogLevel.Error);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Long-running deploys push the default Blazor Server limits:
        //   - DisconnectedCircuitRetentionPeriod defaults to 3 minutes,
        //     after which a disconnected circuit (laptop sleep, flaky
        //     wifi, tab backgrounded) is GC'd and the user sees the
        //     "Could not reconnect to the server. Reload the page to
        //     restore functionality." banner � losing the live log,
        //     the checklist state, and any open approval dialog.
        //   - For a deploy that takes 15-60 minutes that retention is
        //     an order of magnitude too short. We bump it to 1 hour so
        //     the UI survives a coffee break without reload.
        //   - DisconnectedCircuitMaxRetained (default 100) stays put:
        //     we don't expect more than a handful of concurrent users
        //     on a local dev hub.
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(1);
        // Default is 60s; with large live logs + Agent Framework
        // streaming we sometimes hit it. 3 min leaves headroom.
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(3);
    });

builder.Services.AddSignalR(options =>
{
    // Keep the WebSocket alive through corporate / VPN proxies that
    // often idle-kill connections at 30-60s. Tighter heartbeats also
    // let the client DETECT a dead link faster and start reconnecting
    // before the server's retention window lapses.
    options.KeepAliveInterval    = TimeSpan.FromSeconds(10);   // default 15s
    // The client is considered gone only after 2 minutes of silence
    // (default 30s). Covers laptop sleep + most mobile network dips.
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
    // Our live deploy logs can produce single SignalR messages above
    // the default 32 KB cap (e.g. long Doctor JSON outputs); 128 KB
    // is conservative but safer.
    options.MaximumReceiveMessageSize = 128 * 1024;
});

// Persist ASP.NET Core DataProtection keys to a stable path that survives
// container restarts. Without this the runtime falls back to
// /root/.aspnet/DataProtection-Keys (inside the container image layer),
// which is wiped every time the container is recreated � forcing every
// connected user to lose their Blazor Server circuit + all antiforgery
// cookies at each 'docker compose up' cycle. The 'agentichub-state' named
// volume (mounted at /root/.local/share/AgentStationHub by compose) is a
// natural fit: it already holds the agent catalog and memory store.
// On bare-metal dev (no container) the folder resolves under
// %LOCALAPPDATA% on Windows / ~/.local/share on Linux, which is already
// persistent, so this block is a no-op upgrade for native runs too.
var keyRingDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AgentStationHub", "DataProtection-Keys");
Directory.CreateDirectory(keyRingDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir))
    .SetApplicationName("AgentStationHub");

// ---------- Single-account cookie auth ----------
//
// Replaces the previous Caddy `basic_auth` gate with a real /login
// page (Set-Cookie + sliding session). Caddy keeps doing TLS
// termination + reverse proxy, but the credential check now lives in
// the app so we get branded forms, friendly error messages, a
// /logout link, and a session cookie that the SignalR reconnect
// channel inherits automatically. Credentials come from
// `Auth:Username` / `Auth:Password` in configuration (sourced from
// AUTH_USERNAME / AUTH_PASSWORD env vars in docker-compose). When the
// password is empty the gate fails closed — no anonymous access.
// See `Services/Security/SimpleAuth.cs` for the rationale.
builder.Services.AddSimpleAuth();

builder.Services.AddSingleton<AgentCatalogService>();
builder.Services.AddSingleton<AgentMemoryStore>();
builder.Services.AddSingleton<DeploymentSessionStore>();
builder.Services.AddScoped<AzureAIToolkitService>();
builder.Services.AddHttpClient("github", c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AgentStationHub/1.0");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.Timeout = TimeSpan.FromSeconds(10);
});

// -------- Deployment agent wiring --------
builder.Services.Configure<DeploymentOptions>(builder.Configuration.GetSection("Deployment"));

#pragma warning disable OPENAI001, AOAI001 // Azure OpenAI Responses API is in beta
builder.Services.AddSingleton<OpenAIResponseClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured");
    var deployment = cfg["AzureOpenAI:Deployment"]
        ?? throw new InvalidOperationException("AzureOpenAI:Deployment not configured");
    var tenantId = cfg["AzureOpenAI:TenantId"];
    var apiKey = cfg["AzureOpenAI:ApiKey"];

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
    return azureClient.GetOpenAIResponseClient(deployment);
});
#pragma warning restore OPENAI001, AOAI001

builder.Services.AddScoped<PlanExtractorAgent>();
builder.Services.AddScoped<VerifierAgent>();

// EscalationResolverAgent ("Meta-Doctor"): last-line LLM resolver
// invoked by DeploymentOrchestrator when the Doctor returns
// [Escalate] AND the deterministic auto-patch table doesn't match.
// Uses the same Azure OpenAI account as the rest of the host pipeline,
// pinned to the Doctor deployment (o4-mini-class reasoning model).
// Falls back to RunnerDeployment / Deployment if DoctorDeployment
// isn't configured.
#pragma warning disable OPENAI001, AOAI001
builder.Services.AddSingleton<AgentStationHub.Services.Agents.EscalationResolverAgent>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured");
    var deployment = cfg["AzureOpenAI:DoctorDeployment"]
        ?? cfg["AzureOpenAI:RunnerDeployment"]
        ?? cfg["AzureOpenAI:Deployment"]
        ?? throw new InvalidOperationException(
            "AzureOpenAI:DoctorDeployment/RunnerDeployment/Deployment not configured");
    var tenantId = cfg["AzureOpenAI:TenantId"];
    var apiKey   = cfg["AzureOpenAI:ApiKey"];

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
    var chat = azureClient.GetChatClient(deployment);
    var log  = sp.GetRequiredService<ILogger<AgentStationHub.Services.Agents.EscalationResolverAgent>>();
    return new AgentStationHub.Services.Agents.EscalationResolverAgent(chat, log);
});
#pragma warning restore OPENAI001, AOAI001

// Host-side bridge that invokes the multi-agent SandboxRunner inside the
// Docker sandbox for the Planning phase. The legacy PlanExtractorAgent in
// the host remains registered as a fallback. The sandbox image is resolved
// per-deployment by the orchestrator so planning and execution always share
// the same (arch-correct) image.
builder.Services.AddSingleton<AgentStationHub.Services.Tools.SandboxRunnerHost>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new AgentStationHub.Services.Tools.SandboxRunnerHost(
        openAiEndpoint: cfg["AzureOpenAI:Endpoint"] ?? "",
        openAiDeployment: cfg["AzureOpenAI:RunnerDeployment"]
                          ?? cfg["AzureOpenAI:Deployment"] ?? "",
        openAiApiKey: cfg["AzureOpenAI:ApiKey"],
        tenantId: cfg["AzureOpenAI:TenantId"],
        // Per-agent-role deployments. Provisioned in the Foundry account
        // as `ash-strategist` (gpt-5.1), `ash-doctor` (o4-mini reasoning),
        // `ash-verifier` (gpt-4.1-mini). Routed to the SandboxRunner via
        // dedicated env vars so each agent picks the right model. Falls
        // back to RunnerDeployment when unset � legacy single-model
        // behaviour preserved for older configs.
        strategistDeployment: cfg["AzureOpenAI:StrategistDeployment"],
        doctorDeployment:     cfg["AzureOpenAI:DoctorDeployment"],
        verifierDeployment:   cfg["AzureOpenAI:VerifierDeployment"]);
});

builder.Services.AddSingleton<DeploymentOrchestrator>();

// Foundry Hosted-Doctor client. When Foundry:UseFoundryDoctor=true and a
// DoctorAgentEndpoint is configured, the orchestrator routes the
// 'remediate' stage to this hosted agent (registered on the Foundry
// project as 'ash-doctor-hosted'). STRICT MODE: when this flag is on,
// the in-sandbox SandboxRunnerHost Doctor is NOT used as a fall-back —
// any HTTP/auth/agent failure fails the step (per user request: prefer
// cancel + redeploy of the hosted agent over silent degradation).
//
// Hosted reasoning calls can run long � o4-mini emits a large number of
// reasoning tokens before producing the final remediation JSON. Default
// HttpClient timeout (100s) is too tight under load.
builder.Services.AddHttpClient("FoundryDoctor", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
});
{
    // Register only when the feature flag is on AND an endpoint is set.
    // Skipping the registration entirely is cleaner than registering a
    // nullable factory: GetService<FoundryDoctorClient>() returns null
    // out of the box, and the orchestrator's null-check handles the rest.
    var cfg = builder.Configuration;
    if (cfg.GetValue<bool>("Foundry:UseFoundryDoctor"))
    {
        var invokeUrl = cfg["Foundry:DoctorInvokeUrl"];
        if (string.IsNullOrWhiteSpace(invokeUrl))
        {
            var ep = cfg["Foundry:DoctorAgentEndpoint"];
            if (!string.IsNullOrWhiteSpace(ep))
                invokeUrl = ep.TrimEnd('/') + "/invocations";
        }
        if (!string.IsNullOrWhiteSpace(invokeUrl))
        {
            var url = invokeUrl;
            builder.Services.AddSingleton<AgentStationHub.Services.Tools.FoundryDoctorClient>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("FoundryDoctor");
                return new AgentStationHub.Services.Tools.FoundryDoctorClient(
                    http,
                    url,
                    tenantId: cfg["AzureOpenAI:TenantId"],
                    apiKey: cfg["Foundry:DoctorApiKey"]);
            });
        }
    }
}

// Foundry chat-agent client used by the floating AgentChatPanel in the
// nav sidebar. ALWAYS registered (singleton) — the client itself
// reports IsConfigured=false when ProjectEndpoint/AssistantId are blank,
// and the Razor panel renders a "configure your agent" hint instead of
// hitting the wire. This lets us ship the avatar before the user has
// finished building their agent in the Foundry portal.
builder.Services.AddHttpClient("FoundryAgentChat", c =>
{
    c.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddSingleton<AgentStationHub.Services.Tools.FoundryAgentChatClient>(sp =>
{
    var cfg = builder.Configuration;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("FoundryAgentChat");
    // Path B: delegate the whole turn to the Foundry hosted agent
    // (AgentMicrosoftLearn) via the v2 Responses API. ProjectEndpoint
    // is the project-level URL `https://<resource>.services.ai.azure.com/api/projects/<project>`;
    // AgentName is the resolvable agent name in the same project.
    return new AgentStationHub.Services.Tools.FoundryAgentChatClient(
        http,
        projectEndpoint:   cfg["Foundry:ChatAgent:ProjectEndpoint"],
        agentName:         cfg["Foundry:ChatAgent:AgentName"],
        tenantId:          cfg["AzureOpenAI:TenantId"],
        assistantIdLabel:  cfg["Foundry:ChatAgent:AssistantId"]);
});

// GitHub Copilot CLI sidecar. Registered as a singleton AND as a hosted
// service so the image build / container start fires at app boot, before
// the first user click on the home-page button. The hub resolves the
// same singleton instance to spawn `docker exec` sessions on demand.
builder.Services.AddSingleton<CopilotCliService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotCliService>());

// YARP IHttpForwarder: powers the /copilot/ reverse proxy below. We use
// the lower-level forwarder API (not MapReverseProxy with a config) on
// purpose: there is exactly one upstream and one route, so a 4-line
// SendAsync call is clearer than a JSON cluster definition.
builder.Services.AddHttpForwarder();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Only wire the HTTP -> HTTPS redirect when at least one HTTPS endpoint
// is actually listening. In the Docker compose setup we expose only
// :8080 over plain HTTP (TLS is expected to be terminated by an upstream
// proxy or ingress), and registering UseHttpsRedirection in that case
// would log a noisy "Failed to determine the https port for redirect"
// warning on every request. On native dev launches the Kestrel default
// binds https://localhost:7094 alongside http://localhost:5094, so the
// redirect stays enabled and works as expected.
var hasHttpsEndpoint = (app.Urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    || (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "")
        .Contains("https://", StringComparison.OrdinalIgnoreCase)
    || (Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS") ?? "").Length > 0);
if (hasHttpsEndpoint)
{
    app.UseHttpsRedirection();
}

// Honour Caddy's X-Forwarded-Proto / -Host / -For so the cookie auth
// pipeline (and any URL the app generates) sees the original HTTPS
// request scheme. Must run BEFORE UseAuthentication so cookies get
// the Secure flag when the upstream was HTTPS.
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// /login + /logout endpoints. Mapped early so they always win over
// the Blazor catch-all router below.
app.MapSimpleAuthEndpoints();

// ---------- Copilot CLI reverse proxy (YARP IHttpForwarder) ----------
//
// Bridges the Hub's port 8080 to the agentichub-copilot-cli sidecar
// (ttyd) at /copilot/. Two reach modes:
//
//   * In-container (Hub itself runs in Docker on the VM): we hop to the
//     sidecar by service name on the shared compose network.
//     CopilotCliService attaches the sidecar to that network at startup.
//   * Bare-metal (Hub launched via 'dotnet run' on a dev box): the
//     sidecar publishes ttyd on 127.0.0.1:7681; we reach it there.
//
// ttyd is started with '-b /copilot' so its static assets and websocket
// URL are emitted under that base path; the path is forwarded 1:1.
//
// Why YARP IHttpForwarder rather than a hand-rolled HTTP+WS pump:
// the previous hand-rolled implementation accepted the WebSocket and
// connected upstream correctly but dropped the websocket immediately
// after the first ttyd handshake frame, leaving xterm.js stuck on
// "Press to Reconnect". YARP handles WebSocket upgrade, hop-by-hop
// header stripping (RFC 9110 § 7.6.1) and connection lifetime correctly
// and is battle-tested in production proxies.
app.UseWebSockets();
var copilotBackend = CopilotCliService.IsRunningInContainer()
    ? $"http://{CopilotCliService.ContainerName}:7681/"
    : $"http://127.0.0.1:{CopilotCliService.HostPort}/";
// Forwarder needs an HttpMessageInvoker WITHOUT auto-redirect, cookies
// or decompression — exactly the YARP-recommended config.
var copilotForwardClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy            = false,
    AllowAutoRedirect   = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies          = false,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout      = TimeSpan.FromSeconds(15),
});
// No path/query rewriting: ttyd's -b /copilot makes /copilot/* the
// canonical surface, so we forward the request line untouched.
var copilotTransform = HttpTransformer.Default;
var copilotRequestConfig = new ForwarderRequestConfig
{
    ActivityTimeout = TimeSpan.FromSeconds(100),
};
app.Map("/copilot/{**rest}", async (HttpContext ctx, IHttpForwarder fwd) =>
{
    var error = await fwd.SendAsync(ctx, copilotBackend,
        copilotForwardClient, copilotRequestConfig, copilotTransform);
    if (error != ForwarderError.None)
    {
        var feature = ctx.GetForwarderErrorFeature();
        var ex = feature?.Exception;
        ctx.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("CopilotProxy")
            .LogWarning(ex, "Copilot proxy error {Error} for {Path}", error, ctx.Request.Path);
    }
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DeploymentHub>("/hubs/deployment").AllowAnonymous();
// AllowAnonymous: this hub is only consumed by the SERVER-SIDE Blazor
// circuit ([`DeploymentModal.razor`]) which connects to it over the
// container's loopback (`http://127.0.0.1:{port}/hubs/deployment`),
// not the public Caddy origin. The browser never opens this hub
// directly. Blocking unauthenticated access at the app level would
// require forwarding the user's cookie across the loopback hop, which
// is plumbing for no real defence: 127.0.0.1 inside the container is
// unreachable from outside, and the public surface (Caddy → 8080)
// is still gated by the cookie-auth fallback policy on every other
// route. The debug API below has the same shape.

// Debug endpoints — used by autopilot scripts to trigger / observe
// deploys without going through the SignalR + Blazor UI. The app's
// outer port (8080) is only exposed on 127.0.0.1 by docker-compose,
// so any caller is by definition on the host. Plain JSON, no auth.
app.MapPost("/api/debug/deploy/start", (
    DeploymentOrchestrator orch, StartDeployRequest req) =>
{
    var id = orch.Start(req.RepoUrl, req.AzureLocation);
    return Results.Ok(new { sessionId = id });
}).AllowAnonymous();
app.MapPost("/api/debug/deploy/{id}/approve", (string id,
    DeploymentOrchestrator orch) =>
{
    orch.Approve(id);
    return Results.Ok();
}).AllowAnonymous();
app.MapGet("/api/debug/deploy/{id}", (string id,
    DeploymentOrchestrator orch) =>
{
    var s = orch.Get(id);
    if (s is null) return Results.NotFound();
    return Results.Ok(new
    {
        s.Id, s.Status, s.ErrorMessage,
        Plan = s.Plan?.Steps.Select(x => new { x.Id, x.Description, x.Command }),
        LogTail = s.Logs.TakeLast(50).Select(l => new { l.Level, l.StepId, l.Message })
    });
}).AllowAnonymous();

app.Run();

// ---------- Debug DTOs ----------
public record StartDeployRequest(string RepoUrl, string? AzureLocation);
