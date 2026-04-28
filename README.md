# AgentStationHub

A Blazor Server (.NET 8) application — hosted on a dedicated Azure VM — that ties together three operator surfaces:

1. **Hub / Agentic Deploy** — a curated catalog of public Azure AI agent samples plus a *Scan the web* button that queries the GitHub Search API and grows the catalog at runtime. Click **Agentic Deploy** on any card and an AI agent team builds a deployment plan inside a Docker sandbox; you approve it; the orchestrator executes it end-to-end against your Azure subscription with live logs streamed via SignalR. Arbitrary repo URLs (outside the catalog) are accepted only via the debug HTTP API.
2. **Agent Learn** — a floating MS-logo avatar opens an in-app chat that recommends Microsoft Learn modules, paths, certifications, exams and instructor-led courses, powered by Azure OpenAI `gpt-4.1-mini` + a Logic App tool that proxies the live Microsoft Learn catalog.
3. **GitHub Copilot CLI** — a floating launcher pill embeds a real terminal (ttyd) attached to a long-lived sidecar container, reverse-proxied through the Hub's port 8080.

Validated end-to-end (Apr 2026): [`Azure-Samples/azure-ai-travel-agents`](https://github.com/Azure-Samples/azure-ai-travel-agents) (8 services: 3 APIs + 4 MCP servers + UI) deploys to a fresh Azure resource group with **8/8 Container Apps live, 0 failed**, fully autonomously from URL → approve → green. Sandbox image **v32**.

---

## Hosting model

The Hub runs **only on an Azure VM** that you provision once with [`start.ps1`](start.ps1) / [`start.sh`](start.sh). No laptop Docker Desktop, no nested containers on a developer machine.

```
Operator browser ──HTTPS──► https://agentichub-host.eastus2.cloudapp.azure.com
                            │   (Let's Encrypt cert, /login form gates the app)
                            ▼
                            Azure VM (agentichub-host, Standard_D2as_v6, Ubuntu)
                            │
                            ├── docker compose up
                            │     ├── agentichub-caddy  (TLS terminator, :80/:443 public)
                            │     ├── agentichub-app    (Blazor :8080, internal-only)
                            │     └── agentichub-copilot-cli (ttyd sidecar)
                            │
                            └── Sibling sandbox containers spawned per deploy
                                  via /var/run/docker.sock (DooD)
```

- VM lives in resource group `rg-agentichub-host` in `eastus2`. The public surface is **TCP 443 (HTTPS) + TCP 80 (Let's Encrypt HTTP-01 challenge only)** open to `*`, plus TCP 22 to your current public IP/24. Port `8080` is **not** mapped to the host — Caddy is the only thing that talks to the Hub, over the internal docker network. See [Public access & TLS](#public-access--tls).
- `start.ps1` is idempotent: starts the VM if deallocated, syncs the repo + `.env` + `~/.azure` profile, runs `docker compose up -d --force-recreate`, probes `http://<vm-ip>:8080/`, opens the browser.
- `stop.ps1` deallocates the VM (compute billing stops; OS disk + IP remain ~\$3-5/month). `stop.ps1 -Destroy` deletes the whole RG.
- One-time bootstrap: `pwsh .\start.ps1 -Bootstrap` creates the VM, the SSH key (`~/.ssh/agentichub-host`), the system-assigned identity, and bootstraps Docker CE + `docker compose` + Azure CLI on the VM.
- Legacy laptop-only launchers [`start-local.ps1`](start-local.ps1) / [`start-local.sh`](start-local.sh) are kept for reference; they are **not** the supported path.

### Files involved

| File | Purpose |
|---|---|
| [`start.ps1`](start.ps1) / [`start.sh`](start.sh) | VM launcher (provision on first run, sync + compose up on subsequent runs) |
| [`stop.ps1`](stop.ps1) / [`stop.sh`](stop.sh) | Deallocate the VM. `-Destroy` deletes the RG. |
| [`Dockerfile`](Dockerfile) | Multi-stage build of the Hub image (.NET 8 SDK → ASP.NET runtime + `docker` + `az` CLIs + entrypoint) |
| [`docker-compose.yml`](docker-compose.yml) | Hub + Copilot CLI sidecar + path-matching bind mounts on `/var/agentichub-work` and `/var/agentichub-tools` + Docker socket DooD |
| [`entrypoint.sh`](entrypoint.sh) | Seeds `/var/agentichub-tools` from `/runner-src` on first boot, then `exec dotnet` |

---

## Solution layout

| Project | Purpose |
|---|---|
| [`AgentStationHub/`](AgentStationHub/AgentStationHub.csproj) | Blazor Server host. UI, SignalR hub, deployment orchestrator, sandbox image builder, host-side fallback agents, Foundry chat client, Copilot reverse proxy. |
| [`AgentStationHub.SandboxRunner/`](AgentStationHub.SandboxRunner/AgentStationHub.SandboxRunner.csproj) | .NET 8 console that runs **inside** the sandbox container. Hosts the Microsoft.Agents.AI sequential team that produces the deployment plan and the in-sandbox Doctor. |
| [`AgentStationHub.DoctorAgent/`](AgentStationHub.DoctorAgent/AgentStationHub.DoctorAgent.csproj) | Optional Foundry-hosted Doctor agent (Invocations protocol). Used only when `Foundry:UseFoundryDoctor=true`. |

```
AgentStationHub/
├── Program.cs                                 DI, SignalR, Copilot YARP proxy, debug API
├── appsettings.json                           Foundry chat-agent config (committed)
├── appsettings.Development.json               AzureOpenAI endpoints + per-role deployments (gitignored secrets)
├── Components/
│   ├── App.razor / Routes.razor / _Imports.razor
│   ├── Layout/MainLayout.razor                NavMenu + AgentChatPanel + CopilotLauncher overlays
│   ├── Layout/NavMenu.razor
│   ├── Pages/Home.razor                       Landing page
│   ├── Pages/Hub.razor                        Agentic Deploy entry (catalog cards, search/category/target filters, region picker, "Scan the web")
│   ├── Pages/Toolkit.razor                    AI Toolkit info surface
│   ├── DeploymentModal.razor                  Live plan checklist + log + approve/cancel
│   ├── AgentChatPanel.razor                   Floating MS-logo chat (Agent Learn)
│   └── CopilotLauncher.razor                  Floating Copilot CLI pill + iframe
├── Hubs/DeploymentHub.cs                      SignalR: StatusChanged, LogLine, PlanReady
├── Models/                                    DeploymentSession, DeploymentPlan, AgentSample
└── Services/
    ├── DeploymentOrchestrator.cs              Per-session state machine (clone → inspect → plan → approve → execute → verify)
    ├── DeploymentOptions.cs / DeploymentSessionStore.cs
    ├── AgentCatalogService.cs / AgentMemoryStore.cs / AzureAIToolkitService.cs
    ├── CopilotCliService.cs                   Sidecar lifecycle + network attach
    ├── Agents/
    │   ├── PlanExtractorAgent.cs              Host-side fallback planner (Responses API)
    │   └── VerifierAgent.cs                   Post-deploy verdict
    ├── Actions/                               Composable typed actions (AcrBuild, AzdEnvSet, Bash, ContainerAppUpdate)
    ├── Security/PlanValidator.cs              Regex allow-list / deny-list for plan commands
    └── Tools/
        ├── SandboxImageBuilder.cs             Builds agentichub/sandbox:vN on first boot
        ├── SandboxRunnerHost.cs               Bridge to in-container SandboxRunner
        ├── SandboxAzureAuth.cs                Persistent Docker volume for az login cache
        ├── SandboxSession.cs / SandboxWorkspaceVolume.cs
        ├── DockerShellTool.cs                 Per-step docker exec wrapper
        ├── DeploymentProgressWatcher.cs       Silent-phase azd deployment progress probe
        ├── WorkspaceEnvFilePrimer.cs          Pre-creates docker-compose env_file refs
        ├── AzdEnvLoader.cs / AzdEnvSubstitutor.cs
        ├── GitTool.cs / FileTool.cs / RepoInspector.cs
        ├── FoundryDoctorClient.cs             Optional hosted-Doctor HTTP client
        └── FoundryAgentChatClient.cs          Agent Learn: thin client over Foundry Agents v2 Responses API

AgentStationHub.SandboxRunner/
├── Program.cs                                 stdin/stdout JSON protocol (plan / remediate)
├── Contracts/RunnerContracts.cs               DTOs shared with the host
├── Inspection/RepoInspector.cs                In-container scout (direct /workspace FS access)
└── Team/
    ├── PlanningTeam.cs                        Sequential Scout → TechClassifier → Strategist → SecurityReviewer
    └── DoctorToolbox.cs                       In-sandbox Doctor remediation tool

AgentStationHub.DoctorAgent/
├── Program.cs / DoctorInvocationHandler.cs    Foundry Hosted-Agent runtime (Invocations protocol)
├── DoctorBrain.cs                             Reasoning model wrapper (o4-mini)
├── Contracts.cs / Dockerfile
```

---

## Agentic Deploy

The Hub page (`/hub`) shows a curated catalog of public Azure AI agent repositories as cards. Filters narrow the catalog by free-text search, `AgentCategory` (Medical / Legal / Financial / Customer Service / Human Resources / Education / Retail / …) and `DeploymentTarget` (Copilot Studio / Azure AI Foundry / Both). A region dropdown picks the Azure location used by the deploy. The **Scan the web** button calls [`AgentCatalogService.ScanAsync`](AgentStationHub/Services/AgentCatalogService.cs), which fans out a list of curated GitHub Search queries (`search/repositories?q=...&sort=stars`), filters relevant hits, and persists new entries to the `agentichub-state` named volume so they survive restarts. Each card exposes:

- **Agentic Deploy** — hands `agent.GitHubUrl` + the selected region to `DeploymentModal.OpenAsync`, which kicks off the seven-phase orchestrator described below.
- **Open ARM template** (when the catalog entry has `DeployToAzureUrl`) — opens the one-click Deploy-to-Azure URL in a new tab.
- **Open in Copilot Studio** (when the entry targets Copilot Studio) — opens Copilot Studio so the operator can import the solution manually.

Arbitrary repo URLs that are NOT in the catalog can be deployed via the [debug HTTP API](#debug-http-api) (`POST /api/debug/deploy/start`).

Live logs and the plan checklist stream into [`DeploymentModal.razor`](AgentStationHub/Components/DeploymentModal.razor) over SignalR (`/hubs/deployment`). The orchestrator drives seven phases:

### Phases

1. **Cloning** — `git clone` into `/var/agentichub-work/<session-id>/` on the VM. `GitTool` normalises CRLF→LF on `.sh` files (Windows-friendly hosts produce `core.autocrlf=true` checkouts that break shebang parsing inside the Linux sandbox).
2. **Inspecting** (host) — `RepoInspector` builds a toolchain summary (language markers, `azure.yaml` hooks, IaC presence, etc.).
3. **Planning** (sandbox) — orchestrator resolves the sandbox image (native `agentichub/sandbox:vN` on arm64, `azure-dev-cli-apps` on amd64 — see [`SandboxImageBuilder.cs`](AgentStationHub/Services/Tools/SandboxImageBuilder.cs)) and runs `AgentStationHub.SandboxRunner` with two read-only mounts (`/workspace`, `/tools/runner`). The sequential team:
   - **Scout** — direct FS scan, `ToolchainManifest`.
   - **TechClassifier** — gates `deployable=true|false` (notebook curricula short-circuit cleanly instead of burning 10 min on a bogus plan).
   - **DeploymentStrategist** — emits the `DeploymentPlan` JSON (prerequisites, env, steps, verifyHints), pinning subscription + tenant via `azd env set` and one entry per required env var.
   - **SecurityReviewer** — audit pass, returns the amended plan.
   Final plan goes to stdout; agent traces stream to stderr → Live log.
4. **Awaiting approval** — UI renders the plan as a checklist. Nothing has been executed yet.
5. **Executing** — `DeploymentOrchestrator` boots a **single long-lived sandbox container per session** (`asb-<sessionId>`, `docker run -d agentichub/sandbox:v32 sleep infinity`) and dispatches every plan step via `docker exec`. Mounts:
   - `/workspace` — per-session Docker named volume `agentichub-work-<sessionId>` (so `node_modules/.bin/*` keeps the +x bit; bind mounts to Docker Desktop virtio-fs would lose it).
   - `/root/.azure` — persistent named volume `agentichub-azure-profile` (cached `az login` MSAL tokens across deploys).
   - `/root/.docker` — persistent named volume `agentichub-docker-config` (`az acr login` config across deploys).
   Per-step timeouts: `DefaultStepTimeout` (10 min) for short steps, `LongRunningStepTimeout` (60 min) for `azd up` / `azd provision` / `azd deploy`. No session-level wall clock — full multi-service `azd up` runs legitimately exceed 1 h.
   Auxiliary mechanisms wired in:
   - **[`WorkspaceEnvFilePrimer`](AgentStationHub/Services/Tools/WorkspaceEnvFilePrimer.cs)** — `touch`es every `env_file:` ref under `docker-compose*.y?ml` so `docker compose build` in `postprovision` hooks doesn't fail with `ENOENT`.
   - **Post-`azd up`/`azd provision` ACR login hook** — derives ACR from `azd env get-values` and runs `az acr login`, persisting creds into the docker-config volume.
   - **[`DeploymentProgressWatcher`](AgentStationHub/Services/Tools/DeploymentProgressWatcher.cs)** — every 30 s emits `[progress hh:mm] N azd deployment(s): X succeeded, Y running, Z failed` so the UI never looks frozen during silent phases.
   - **Self-healing Doctor** — on non-zero exit, the orchestrator invokes `remediate` on the runner. Either the **in-sandbox Doctor** (default) or the **Foundry-hosted Doctor** (when `Foundry:UseFoundryDoctor=true`) returns a structured `Remediation { kind: "replace_step" | "insert_before" | "give_up" }`. The orchestrator mutates the live plan, renumbers step IDs, re-emits `PlanReady`, and retries. Capped by `DeploymentOptions.MaxDoctorInvocationsPerSession` (default 8). Probe steps tagged `[Probe] ` in `description` don't consume the budget.
6. **Verifying** — [`VerifierAgent`](AgentStationHub/Services/Agents/VerifierAgent.cs) reads the last 40 log lines + plan `verifyHints`, returns success/failure + extracted endpoint URL.
7. **Succeeded / Failed** — UI shows clickable endpoint or a red diagnostics block with the exact error tail.

Durable sessions ([`DeploymentSessionStore`](AgentStationHub/Services/DeploymentSessionStore.cs)): every session is persisted to `%LOCALAPPDATA%/AgentStationHub/sessions/<id>.json` (mapped to the `agentichub-state` named volume in the container). Browser reload + VM restart resume the session; non-terminal sessions interrupted by an app restart are transitioned to `Failed` with an explicit "Interrupted by app restart" marker.

### Sandbox image

Built on demand by `SandboxImageBuilder` (tag `agentichub/sandbox:vN`, currently **v32**). Effective base: `mcr.microsoft.com/azure-cli:latest` + `tdnf install` of `dotnet-runtime-8.0`, git, python3, jq, zip/unzip, tar; node 20 LTS unpacked from `nodejs.org`; `pip install uv`; `azd` from `aka.ms/install-azd.sh`; `azd config set auth.useAzCliAuth true`. `/usr/local/bin` ships the `agentic-*` helper toolbox (`agentic-azd-up`, `agentic-acr-build`, `agentic-build`, `agentic-npm-install`, `agentic-dotnet-restore`, `agentic-bicep`, `agentic-clone`, `agentic-aca-wait`, `agentic-summary`, `agentic-help`, `relocate-node-modules`, `relocate-venv`, `agentic-azd-env-prime`) so the Strategist composes plans from finite single-token commands instead of multi-line nested-quote shell. `PlanValidator` allow-lists every helper.

### Models

| Component | API | Default deployment | Configured in |
|---|---|---|---|
| `PlanExtractorAgent` (host fallback) | Responses | `gpt-5.4` | `AzureOpenAI:Deployment` |
| `VerifierAgent` (host) | Responses | `ash-verifier` | `AzureOpenAI:VerifierDeployment` |
| Strategist (sandbox) | Chat Completions | `ash-strategist` | `AzureOpenAI:StrategistDeployment` |
| Doctor (sandbox or hosted) | Chat Completions / Invocations | `ash-doctor` (`o4-mini`) | `AzureOpenAI:DoctorDeployment` |
| Other runner agents | Chat Completions | `gpt-5.3-chat` | `AzureOpenAI:RunnerDeployment` |
| Agent Learn chat | Foundry Responses (hosted agent) | `gpt-4.1-mini-1` | `Foundry:ChatAgent:AgentName` |

---

## Agent Learn (in-app chat)

Floating circular Microsoft-logo avatar pinned bottom-left of every page (registered in [`MainLayout.razor`](AgentStationHub/Components/Layout/MainLayout.razor)). Clicking it opens a 360×520 chat panel with header *Agent Learn*. The agent recommends Microsoft Learn modules, paths, certifications, exams and instructor-led courses in fluent natural language with Markdown citations to real `learn.microsoft.com` URLs — and grounds explanatory questions on the official docs surface via the Microsoft Learn MCP server.

The in-app chat **delegates the entire turn** to the Foundry hosted agent `AgentMicrosoftLearn` (configured in the AgenticStationFoundry portal, project `default`). The agent owns instructions, tool routing, and grounding — what users see in the panel is byte-for-byte what the playground returns.

```
[User] ──► [Blazor Server: AgentChatPanel]
                   │  per-circuit threadId = previous_response_id (multi-turn memory)
                   ▼
          [FoundryAgentChatClient]  (thin client, no system prompt, no tool routing)
                   │  POST {ProjectEndpoint}/openai/v1/responses
                   │  Authorization: Bearer <DefaultAzureCredential, scope https://ai.azure.com/.default>
                   │  body: { agent_reference:{type:"agent_reference",name:"AgentMicrosoftLearn"},
                   │          input:[{role:"user",content:[{type:"input_text",text:...}]}],
                   │          previous_response_id?:"resp_…" }
                   ▼
          [Foundry Agent Service — AgenticStationFoundry / project=default]
                   │  AgentMicrosoftLearn (kind=prompt, model=gpt-4.1-mini-1, v9)
                   │   ├─ openapi tool: microsoft_learn_catalog → Logic App `logicapp-090730`
                   │   └─ mcp tool: MicrosoftLearnMCPserver → https://learn.microsoft.com/api/mcp
                   │       (microsoft_docs_search / microsoft_docs_fetch / microsoft_code_sample_search)
                   ▼
          response.output[].type=="message" → output_text  (with learn.microsoft.com Markdown links)
                   ▼
          [User]
```

- **Endpoint**: `POST {ProjectEndpoint}/openai/v1/responses` — no `api-version` query param (the `/openai/v1` path rejects it). ProjectEndpoint = `https://agenticstationfoundry.services.ai.azure.com/api/projects/default`.
- **Auth**: `DefaultAzureCredential` fetches a token with scope `https://ai.azure.com/.default`. The principal needs role **`Azure AI User`** on the project scope (`…/accounts/AgenticStationFoundry/projects/default`). On the VM the `ash-doctor-orchestrator` SP has been granted both `Cognitive Services OpenAI User` (account scope) and `Azure AI User` (project scope).
- **Multi-turn memory**: the agent's `response.id` is reused as the next call's `previous_response_id`, giving the panel conversation memory inside the same Blazor circuit. A hard reload starts a fresh thread.
- **Tools live in the portal, not in the code**: `microsoft_learn_catalog` (Logic App `logicapp-090730`, anonymous SAS) and the Microsoft Learn MCP server are attached to `AgentMicrosoftLearn` in the Foundry portal. Editing tools / instructions / model never requires re-deploying the Hub.
- **Why this beats the previous AOAI bypass**: the old client only had the catalog tool and ran via chat-completions; it could not ground on official docs. With the hosted agent the Hub gets MCP-grounded answers (citing real `learn.microsoft.com` URLs) for free.
- **Legacy keys** (`Foundry:ChatAgent:OpenAiEndpoint`, `Deployment`, `LearnToolUrl`) are kept in `appsettings.json` for reference but ignored by the new client.

---

## GitHub Copilot CLI sidecar

Floating pill pinned to the bottom-right of every route ([`CopilotLauncher.razor`](AgentStationHub/Components/CopilotLauncher.razor)). Opens a 760×480 panel containing `<iframe src="/copilot/">`.

- **Sidecar image**: `agentichub/copilot-cli:latest`, built from `Dockerfiles/copilot-cli.Dockerfile` (Node 20 slim + `util-linux` + `gh` + `npm i -g @github/copilot` + `ttyd`).
- **Container**: `agentichub-copilot-cli`, started once at app boot by [`CopilotCliService`](AgentStationHub/Services/CopilotCliService.cs) (`IHostedService`).
- **Persistent home**: named volume `agentichub-copilot-home` mounted at `/root` so `gh` / Copilot tokens + shell history survive restarts.
- **Reverse proxy** (`/copilot/*` → ttyd) — wired in [`Program.cs`](AgentStationHub/Program.cs) using **YARP `IHttpForwarder`**. ttyd runs with `-b /copilot` so static assets and the websocket URL are emitted under that base path. On the VM, `CopilotCliService` attaches the sidecar to the Hub's compose network instead of publishing a host port — ttyd is reachable only through the Hub's authenticated origin (no second port, no NSG hole). Bare-metal `dotnet run` keeps the historical `127.0.0.1:7681` host publish so dev launches still work.
- YARP was chosen over a hand-rolled HTTP+WS pump because the latter dropped the websocket immediately after ttyd's first frame, leaving xterm.js stuck on "Press to Reconnect"; YARP's forwarder handles WebSocket upgrade and hop-by-hop header stripping (RFC 9110 § 7.6.1) out of the box.

---

## Configuration reference

### `AgentStationHub/appsettings.json` (committed)

```jsonc
{
  "Foundry": {
    "UseFoundryDoctor": false,         // when true, route Doctor calls to a Foundry Hosted Agent (strict, no fallback)
    "DoctorAgentEndpoint": "",
    "DoctorInvokeUrl": "",             // optional override; default = DoctorAgentEndpoint + "/invocations"
    "DoctorApiKey": "",                // optional; otherwise DefaultAzureCredential
    "ChatAgent": {                     // Agent Learn (floating MS-logo chat)
      "OpenAiEndpoint": "https://agenticstationfoundry.openai.azure.com",
      "Deployment": "gpt-4.1-mini-1",
      "LearnToolUrl": "https://logicapp-090730.azurewebsites.net/api/Get_Learning_Content/triggers/Request/invoke?api-version=...&sig=...",
      "AssistantId": "Agent Learn"     // cosmetic label shown in the panel header
    }
  }
}
```

### `AgentStationHub/appsettings.Development.json` (gitignored — secrets)

```jsonc
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource>.openai.azure.com/",
    "Deployment": "gpt-5.4",                  // PlanExtractor (host fallback, Responses API)
    "RunnerDeployment": "gpt-5.3-chat",       // Other runner agents (Chat Completions)
    "StrategistDeployment": "ash-strategist", // Strategist (Chat Completions)
    "DoctorDeployment": "ash-doctor",         // Doctor (Chat Completions / Invocations, o4-mini)
    "VerifierDeployment": "ash-verifier",     // Verifier (Responses API)
    "TenantId": "<aad-tenant-id>",
    "ApiKey": "<optional; omit to use DefaultAzureCredential>"
  },
  "Deployment": {
    "AutoApprove": false,
    "SandboxImage": "mcr.microsoft.com/azure-dev-cli-apps:latest"
  }
}
```

### `Deployment` options ([`DeploymentOptions.cs`](AgentStationHub/Services/DeploymentOptions.cs))

| Key | Default | Purpose |
|---|---|---|
| `SandboxImage` | `mcr.microsoft.com/azure-dev-cli-apps:latest` | Auto-swapped to `agentichub/sandbox:vN` on arm64 hosts and on amd64 by `SandboxImageBuilder.ResolveAsync` |
| `AutoApprove` | `false` | Skip the Approval phase (debug only) |
| `DefaultStepTimeout` | `00:10:00` | Generic step timeout |
| `LongRunningStepTimeout` | `01:00:00` | Applied to `azd up` / `azd provision` / `azd deploy` |
| `WorkRootDir` | `null` (= `/var/agentichub-work`) | Per-session clone root |
| `DefaultAzureLocation` | `eastus` | Pre-selected region in the Hub UI |
| `AvailableAzureLocations` | `["eastus", "westeurope", "swedencentral", ...]` | Region dropdown |
| `MaxDoctorInvocationsPerSession` | `8` | Hard cap on remediation attempts |

The Azure region used by a deploy is resolved with this precedence: (1) UI dropdown selection, (2) `Deployment.DefaultAzureLocation`, (3) hard fallback `eastus`. The selected region is passed to the Strategist as `TARGET REGION: <region>` and used for `AZURE_LOCATION` and any sibling `*_LOCATION` / `*_REGION` vars detected by the Scout.

### Environment variables (`.env` on the VM repo root)

Read by `docker-compose.yml`. Notably:

- `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` / `AZURE_TENANT_ID` — when set, `DefaultAzureCredential` inside the container picks up the SP (`ash-doctor-orchestrator`) instead of the VM's system-assigned MI.
- `FOUNDRY_USE_FOUNDRY_DOCTOR=true` and `FOUNDRY_DOCTOR_AGENT_ENDPOINT=...` — enable the Foundry Hosted Doctor.
- `PUBLIC_FQDN` — public hostname Caddy will request a Let's Encrypt cert for. Defaults to `agentichub-host.eastus2.cloudapp.azure.com` (Azure-provided FQDN attached to the VM's public IP).
- `AUTH_USERNAME` / `AUTH_PASSWORD` — the single account that gates the public surface, validated by the app's `/login` page (cookie auth, see [`Services/Security/SimpleAuth.cs`](AgentStationHub/Services/Security/SimpleAuth.cs)). Plain string, kept only in `.env` on the VM (gitignored). When unset the app fails closed — nobody can authenticate.
- `CADDY_ACME_EMAIL` — contact for the ACME account. Must be a syntactically valid email with a public TLD; Let's Encrypt rejects `.local`. Using something like `admin@<PUBLIC_FQDN>` works.

---

## Public access & TLS

The VM is reachable from the open internet at **`https://agentichub-host.eastus2.cloudapp.azure.com`** behind a [Caddy v2](https://caddyserver.com/) reverse proxy, and the app itself ships a real **`/login` page** (cookie auth) that gates every route. The full posture is:

```
Internet ──► NSG (TCP 443 + 80 from *, TCP 22 from your /24)
        ──► VM eth0
        ──► docker host:443/80
        ──► agentichub-caddy (TLS termination + security headers)
        ──► docker network ──► agentichub-app:8080  (no host port published)
                                  └─ ASP.NET Core cookie auth
                                     └─ unauth → 302 /login
```

What this gives us:

- **Auto-TLS via Let's Encrypt HTTP-01.** Caddy provisions and renews the cert with no manual step, persisting account key + chain in the named volumes `agentichub-caddy-data` / `agentichub-caddy-config`. ZeroSSL is the configured fallback.
- **Branded login page in the app.** [`Services/Security/SimpleAuth.cs`](AgentStationHub/Services/Security/SimpleAuth.cs) wires `AddAuthentication().AddCookie()` with a fallback `RequireAuthenticatedUser` policy, so any unauthenticated request to any path (Blazor route, debug API, `/copilot/*`) is redirected to `/login?ReturnUrl=...`. The page is a single self-contained inline HTML form (no Razor view, no static-file dependency) with constant-time credential comparison via `CryptographicOperations.FixedTimeEquals`. A `Sign out` link in the sidebar hits `/logout`, which clears the cookie and redirects back to `/login`. Sliding 7-day session cookies (`agentichub_auth`, `HttpOnly`, `SameSite=Lax`, `Secure` once Caddy upgrades the request to HTTPS via `X-Forwarded-Proto`).
- **Hub origin not exposed.** `agentichub-app` declares `expose: ["8080"]` instead of `ports:`, so 8080 is not bound on the host. The only path to the Hub is via Caddy on the same docker network. The `agentichub-copilot-cli` sidecar continues to be attached to the same network — `/copilot/*` is YARP-forwarded inside the Hub, so the same edge auth applies to the terminal too.
- **Hardening headers.** [`Caddyfile`](Caddyfile) sets HSTS (`max-age=31536000; includeSubDomains`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Frame-Options: SAMEORIGIN`, removes the `Server` header, and enables gzip + zstd encoding.
- **HTTP→HTTPS 308 redirect** is automatic. Plain HTTP only stays open so the ACME HTTP-01 challenge can complete.
- **Forwarded headers honoured.** The app calls `UseForwardedHeaders` for `X-Forwarded-Proto/Host/For` so cookie auth, redirect URLs and antiforgery tokens reflect the original HTTPS request rather than the internal `http://agentichub:8080` hop.

NSG layout in `rg-agentichub-host` / `agentichub-hostNSG`:

| Rule | Priority | Source | Dest port | Purpose |
|---|---|---|---|---|
| `AllowSshFromMyIp` | 1010 | your `/24` | 22 | SSH (refreshed by `start.ps1`) |
| `AllowHTTP` | 1020 | `*` | 80 | Let's Encrypt HTTP-01 + 308 to HTTPS |
| `AllowHTTPS` | 1030 | `*` | 443 | Public app traffic (`/login` gated) |

Credentials are not in the repo. They live only in `~/agentichub/.env` on the VM as `AUTH_USERNAME` / `AUTH_PASSWORD`. To rotate, edit `.env` and `sudo docker compose up -d --force-recreate --no-deps agentichub`.

Smoke test from any machine:
```
curl -I https://agentichub-host.eastus2.cloudapp.azure.com/                  # 302 -> /login
curl    https://agentichub-host.eastus2.cloudapp.azure.com/login | head      # the form
curl -c /tmp/c -X POST -d 'username=<u>&password=<p>' \
        https://agentichub-host.eastus2.cloudapp.azure.com/login              # 302 + Set-Cookie
curl -b /tmp/c -I https://agentichub-host.eastus2.cloudapp.azure.com/         # 200
```

Files involved: [`AgentStationHub/Services/Security/SimpleAuth.cs`](AgentStationHub/Services/Security/SimpleAuth.cs), [`Caddyfile`](Caddyfile), [`docker-compose.yml`](docker-compose.yml) (`caddy` service + `caddy-data` / `caddy-config` volumes + `Auth__Username` / `Auth__Password` env).

---

## Debug HTTP API

The Hub's port 8080 is bound only to the internal docker network (no host publish), so these endpoints are only reachable from inside the VM (e.g. via `docker exec` or by going through Caddy with basic auth). Three unauthenticated debug endpoints help autopilot scripts drive deploys without the Blazor UI:

| Method | Path | Body | Returns |
|---|---|---|---|
| `POST` | `/api/debug/deploy/start` | `{ "repoUrl": "...", "azureLocation": "..." }` | `{ "sessionId": "..." }` |
| `POST` | `/api/debug/deploy/{id}/approve` | — | `200` |
| `GET` | `/api/debug/deploy/{id}` | — | `{ Id, Status, ErrorMessage, Plan, LogTail }` |

---

## Resetting state

| What | How |
|---|---|
| Clear sandbox Azure login | `docker volume rm agentichub-azure-profile` |
| Clear sandbox Docker registry creds | `docker volume rm agentichub-docker-config` |
| Clear discovered-agents catalog + memory | `docker volume rm agentichub-state` |
| Clear Copilot CLI auth + history | `docker volume rm agentichub-copilot-home` |
| Force rebuild of the Copilot CLI sidecar | `docker rm -f agentichub-copilot-cli; docker rmi agentichub/copilot-cli:latest` |
| Clear per-deploy workspaces | `sudo rm -rf /var/agentichub-work/* /var/agentichub-tools/*` then `docker compose restart` |

---

## Key design decisions

- **Sandbox-first execution**. Every command the user approves runs in a Docker sandbox container on the VM. Failures are contained.
- **Agent team in a container, not in the host**. Direct FS access to the cloned repo for the Scout, no agent-executed code on the Hub container, separate Azure login surface.
- **Plan validation before execution**. [`PlanValidator`](AgentStationHub/Services/Security/PlanValidator.cs) blocks dangerous commands (`rm -rf /`, `curl | sh`, remote exec, writes outside the repo) before the approval modal is shown.
- **Explicit approval**. No auto-pilot. The user reads the plan, then decides. Doctor remediations re-emit `PlanReady` so the live checklist always reflects what is actually running.
- **Streaming observability**. SignalR + stderr piping → every agent thought, every Docker line, every exit code is visible in real time.
- **Helper-first plans**. The Strategist composes plans from a finite set of single-token `agentic-*` helpers baked into the sandbox image, never multi-line nested-quote bash.

---

## Recent changes

- **Strategist server-side guard for repos without `azure.yaml`** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). When the Strategist's first plan emits `azd up` / `azd provision` / `azd deploy` / `azd env new` against a repo that has no `azure.yaml`, the orchestrator now (a) detects the violation deterministically with a regex over the plan JSON, (b) re-prompts the Strategist once with an explicit "Strategy 1b - Bicep-direct" directive embedded inline, and (c) feeds the corrected plan to the Reviewer. The Strategist input now also carries a banner-style `HARD CONSTRAINT - NO 'azure.yaml' AT THE REPO ROOT` block listing the forbidden commands and the required Bicep-direct flow (`az group create` → `az deployment group create` → `agentic-acr-build` → `az containerapp update`). Triggered by [`Azure-Samples/Agentic-AI-Investment-Analysis-Sample`](https://github.com/Azure-Samples/Agentic-AI-Investment-Analysis-Sample): the repo ships `infra/*.bicep`, per-app `Dockerfile`s and shell deploy scripts, but no `azure.yaml`. Without the guard the Strategist still hallucinated `azd env new` (despite the prompt-side rule), the Doctor escalated, and the deploy ended in `BlockedNeedsHumanOrSourceFix` even though Strategy 1b would have been viable. The new behaviour gives the Strategist a second chance backed by the inspector's ground-truth on `azure.yaml`.
- **`[Escalate]` is now an INFO outcome, not a red error** ([`AgentStationHub/Models/DeploymentSession.cs`](AgentStationHub/Models/DeploymentSession.cs), [`Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs), [`Components/DeploymentModal.razor`](AgentStationHub/Components/DeploymentModal.razor)). New terminal status `BlockedNeedsHumanOrSourceFix`: when the Doctor (in-sandbox or hosted Foundry) emits `give_up` with reasoning starting `[Escalate]`, the orchestrator now (a) downgrades the relevant log lines from `err` to `info`, (b) sets the session to `BlockedNeedsHumanOrSourceFix` instead of `Failed`, and (c) the Hub modal renders an `alert-info` panel ("Deployment blocked — needs a fix on the source repo") instead of the red `alert-danger` "Deployment error" box. Rationale: when the Doctor correctly identifies that the failure is rooted in the repo source itself (missing `azure.yaml`, broken Bicep, corrupt lockfile), the pipeline did its job — the next move is on the user (PR on the source) or on picking a different sample. Treating that as a deployment failure was UX-misleading.
- **`start.ps1` repo-sync now hard-excludes `.env` and `appsettings.Development.json`** ([`start.ps1`](start.ps1)). The repo-archive step previously had no protection for the on-VM `.env` (gitignored, secrets-only) — a stray local copy or accidental rsync-style sync would silently overwrite the production secrets file at `/home/azureuser/agentichub/.env`, leaving the container with empty `AzureOpenAI__Endpoint` and crashing every deploy with `System.UriFormatException: Invalid URI: The URI is empty.` at `Program.cs` `new Uri(endpoint)`. The tar archive now `--exclude='./.env' --exclude='**/appsettings.Development.json'` so the on-VM secrets file is treated as VM-side state, not source. Triggered by a real incident on `agentichub-host` where the file was wiped and the Hub modal stalled at `PENDING` for every deploy until `.env` was restored from `appsettings.Development.json` + base64 heredoc via `az vm run-command invoke`.
- **Strategist + Doctor: no-scaffolding repos must escalate, not invent commands** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs), [`AgentStationHub.DoctorAgent/DoctorBrain.cs`](AgentStationHub.DoctorAgent/DoctorBrain.cs)). When the repo lacks `azure.yaml`, the Strategist is now explicitly forbidden from emitting `azd up` / `azd provision` / `azd deploy` / `azd env new` and from inventing scaffolding commands like `azd init --template-empty` / `azd init --from-code`. It must instead pick a strategy that uses ONLY the artifacts the authors actually shipped (Bicep-direct via `az deployment group create` + `agentic-acr-build`, Terraform, docker-compose, README-documented deploy, …) or escalate via `[Escalate] repository ships no deployment artifacts`. The Doctor (both in-sandbox and Foundry-hosted) gets the same hard rule: when the failing step is `azd <x>` against a repo without `azure.yaml`, it must emit `give_up` with `[Escalate] ` rather than fabricate commands. Triggered by a deploy attempt of `Azure-Samples/Agentic-AI-Investment-Analysis-Sample` (no `azure.yaml`, only local-dev README) where the Strategist hallucinated `azd env new` and the deploy died with `ERROR: no project exists`.
- **README accuracy pass** — corrected the Hub UX description: there is no free-form "paste a repo URL" input, the Hub is a curated catalog with filters and a *Scan the web* growth path; arbitrary URLs go through the debug API. Project layout updated accordingly. The `?` badge in the nav opens an `/about` page that renders this README at runtime via Markdig.
- **Agent Learn — floating chat avatar in the Hub sidebar** ([`AgentChatPanel.razor`](AgentStationHub/Components/AgentChatPanel.razor), [`FoundryAgentChatClient.cs`](AgentStationHub/Services/Tools/FoundryAgentChatClient.cs), wiring in [`Program.cs`](AgentStationHub/Program.cs)). MS-logo avatar opens a chat that delegates every turn to the **Foundry hosted agent `AgentMicrosoftLearn`** via the v2 Responses API (`POST {project}/openai/v1/responses` with `agent_reference`). The portal-side agent owns instructions and routes between the `microsoft_learn_catalog` Logic App and the Microsoft Learn MCP server, so answers come back grounded on official docs with `learn.microsoft.com` Markdown citations — identical to what users see in the Foundry playground. `?` tooltip next to the title surfaces the architecture summary inline.
- **VM-only hosting model**. `start.ps1` / `start.sh` rewritten (Apr 2026) to provision and drive a dedicated Azure VM. The legacy local-Docker launchers are kept as `start-local.ps1` / `start-local.sh` for reference but are not the supported flow.
- **Copilot CLI reverse proxy via YARP `IHttpForwarder`**. `/copilot/*` is forwarded to the sidecar's ttyd; on the VM the sidecar is attached to the Hub's compose network with no host port publish.
- **v32 sandbox + amd64 fixes (Apr 2026)** — closed the 8/8 saga on `azure-ai-travel-agents`:
  1. `agentic-azd-up` strips BuildKit-only `RUN --mount=` flags from generated Dockerfiles before invoking `az acr build`.
  2. `az containerapp update` stderr is captured into `/tmp/caup-<svc>.log`; the last 40 lines are embedded in the per-service fail reason.
  3. `SandboxImageBuilder.ResolveAsync` swaps the upstream `azure-dev-cli-apps` image for the locally-built `agentichub/sandbox` image on **both** arm64 and amd64 hosts (was previously arm64-only).
  4. The heavy-step output guard whitelists fast-path skip markers (`skipping redundant`, `already have real images`, `nothing to do`, `✓ all`) so an idempotent no-op `agentic-azd-deploy` is no longer mis-classified as a broken pipe.
- **`agentic-*` helper toolbox baked into the sandbox image**. Plans are composed from finite single-token helpers; `PlanValidator` allow-lists every one and rejects `bash -lc "..."` payloads with 4+ levels of escaping. Includes `[Probe]` tag for diagnostic steps (don't consume Doctor budget) and `[Escalate]` verdict for repo-source bugs (UI prompts the user to fix the source instead of burning attempts).
- **Foundry hosted DeploymentDoctor** ([`AgentStationHub.DoctorAgent/`](AgentStationHub.DoctorAgent/), [`FoundryDoctorClient.cs`](AgentStationHub/Services/Tools/FoundryDoctorClient.cs)). Optional, gated by `Foundry:UseFoundryDoctor=true`. Strict-Foundry-only when enabled (no fallback to in-sandbox Doctor) — by design, cancel + redeploy the agent rather than silently degrade. Reasoning model `o4-mini` (deployment `ash-doctor`) on Foundry Agent Service.
- **Durable deployment sessions** ([`DeploymentSessionStore.cs`](AgentStationHub/Services/DeploymentSessionStore.cs)). Sessions persist to `agentichub-state` volume; non-terminal sessions interrupted by an app restart transition to `Failed` with an explicit marker.
- **Three-layer defense for Node `NoexecBindMount`**. Strategist preventive injection (symlink `node_modules` to `/tmp/nm-<slug>`), last-resort canonical fix in the orchestrator after Doctor budget exhaustion, indirect classifier branch for npm usage-page output patterns.
- **Fresh resource group per deploy**. `EnforceUniqueAzdEnvName` appends `-yyyyMMdd-HHmm` to the azd env name so every run lands in a brand-new RG.

---

## License

MIT (or match the parent organisation's default).
