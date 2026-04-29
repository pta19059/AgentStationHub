# AgentStationHub

A Blazor Server (.NET 8) application — hosted on a dedicated Azure VM — that ties together three operator surfaces:

1. **Hub / Agentic Deploy** — a curated catalog of public Azure AI agent samples plus a *Scan the web* button that queries the GitHub Search API and grows the catalog at runtime. Click **Agentic Deploy** on any card and an AI agent team builds a deployment plan inside a Docker sandbox; you approve it; the orchestrator executes it end-to-end against your Azure subscription with live logs streamed via SignalR. Arbitrary repo URLs (outside the catalog) are accepted only via the debug HTTP API.
2. **Agent Learn** — a floating MS-logo avatar opens an in-app chat that recommends Microsoft Learn modules, paths, certifications, exams and instructor-led courses, powered by Azure OpenAI `gpt-4.1-mini` + a Logic App tool that proxies the live Microsoft Learn catalog.
3. **GitHub Copilot CLI** — a floating launcher pill embeds a real terminal (ttyd) attached to a long-lived sidecar container, reverse-proxied through the Hub's port 8080.

Validated end-to-end (Apr 2026): [`Azure-Samples/azure-ai-travel-agents`](https://github.com/Azure-Samples/azure-ai-travel-agents) (8 services: 3 APIs + 4 MCP servers + UI) deploys to a fresh Azure resource group with **8/8 Container Apps live, 0 failed**, fully autonomously from URL → approve → green. Sandbox image **v34**.

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
5. **Executing** — `DeploymentOrchestrator` boots a **single long-lived sandbox container per session** (`asb-<sessionId>`, `docker run -d agentichub/sandbox:v34 sleep infinity`) and dispatches every plan step via `docker exec`. Mounts:
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

Built on demand by `SandboxImageBuilder` (tag `agentichub/sandbox:vN`, currently **v34**). Effective base: `mcr.microsoft.com/azure-cli:latest` + `tdnf install` of `dotnet-runtime-8.0`, git, python3, jq, zip/unzip, tar; node 20 LTS unpacked from `nodejs.org`; `pip install uv`; `azd` from `aka.ms/install-azd.sh`; `azd config set auth.useAzCliAuth true`. `/usr/local/bin` ships the `agentic-*` helper toolbox (`agentic-azd-up`, `agentic-acr-build`, `agentic-build`, `agentic-npm-install`, `agentic-dotnet-restore`, `agentic-bicep`, `agentic-clone`, `agentic-aca-wait`, `agentic-summary`, `agentic-help`, `relocate-node-modules`, `relocate-venv`, `agentic-azd-env-prime`) so the Strategist composes plans from finite single-token commands instead of multi-line nested-quote shell. `PlanValidator` allow-lists every helper.

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

- **AgentMemoryStore: persist Doctor fixes + give-up signatures across sessions; cache `ToolchainManifest` in the runner** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs), [`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). Three coordinated improvements (8.3 + 8.8 + 8.10) that take the AgentMemoryStore from a "best-known last region" hint into a real cross-session learning surface for the self-healing loop. (1) **8.3 — proven-fix recall**: when the Doctor (or the EscalationResolver, or a deterministic auto-patch) applies a `replace_step` / `insert_before` and the next iteration of the step loop reaches `exit=0`, the orchestrator persists the fix as `_memory.UpsertInsight(repoUrl, $"doctor.fix.{errSig}", command, 0.85)`. The error signature comes from `SummariseErrorSignature(stepTail)` so the key is stable across deploys (e.g. `doctor.fix.InvalidResourceLocation`, `doctor.fix.ContainerAppSecretInvalid`). Because the Doctor and the EscalationResolver already receive `_memory.GetRelevantInsights(repoUrl)` in their input envelope, the very next deploy of the same repo sees the proven command listed alongside the prior facts before a single LLM token is generated — the agent transitions from "re-discover every fix" to "apply known good fixes before speculating". A `pendingDoctorAttribution` slot tracks the most recent applied fix; it is overwritten on each new Doctor application and cleared on success. (2) **8.8 — failure-pattern store + cross-session `previousAttempts` injection**: when the final Doctor verdict is `give_up` (no auto-patch, no resolver fix), the orchestrator now writes BOTH the existing `doctor.lastGiveUp` rationale AND a new per-signature key `doctor.giveup.{errSig}` whose value is the last 8 entries of the in-session `previousAttempts` list (newline-joined, each line truncated to 180 chars). On the next session, just before the FIRST Doctor invocation against the same `(repoUrl, errSig)` pair, the orchestrator reads that insight back, prefixes each line with `[prior-session FAILED]`, and `Insert(0, …)`s them into `previousAttempts` (with dedup). The Doctor's `previousAttempts` envelope therefore now contains both the in-session attempts and the failed strategies from prior runs, so it pivots on the first turn instead of re-trying known dead-ends. A `HashSet<string> injectedHistoricalSigs` ensures we only inject once per signature per session. (3) **8.10 — `ToolchainManifest` cache in the runner**: `PlanningTeam.RunAsync` (planning) and `RemediateAsync` (remediation) both called `RepoInspector.Inspect(workspace)` unconditionally, walking the repo tree (100–500 ms on large samples). They now go through `GetOrInspectManifest`, a static cache keyed by `(workspace, fingerprint)`. The fingerprint is intentionally cheap: `max(LastWriteTimeUtc)` across a curated set of toolchain manifest files at the repo root (`Dockerfile`, `docker-compose.yml`, `package.json`, `pyproject.toml`, `azure.yaml`, `*.csproj`, `*.tf`, `*.bicep`, …) plus their count — walking `node_modules` / `.venv` would defeat the purpose. Today the SandboxRunner is short-lived (one `docker run --rm` per command) so the cache is mostly inert across calls; within a single process invocation it ensures a SECOND `Inspect` call on the same workspace is free, and once the runner moves to a long-lived daemon (or planner+remediate share a process) the cache pays off automatically without further code changes. All three changes are non-fatal: persistence failures are caught + logged at `Debug` level, the cache falls through to a fresh `Inspect` on any IO error.
- **`AzureModelCatalogProbe`: live AOAI catalog grounding for the EscalationResolver** ([`AgentStationHub/Services/Tools/AzureModelCatalogProbe.cs`](AgentStationHub/Services/Tools/AzureModelCatalogProbe.cs)). The hand-coded `versionReplacement` table inside Pattern C ages out every time Azure retires a model or version — and when it lags behind, the resolver hallucinates a `(name, version)` pair that ARM rejects, the Doctor counter-proposes a revert, and the orchestrator burns 8-13 self-healing attempts ping-ponging between two equally-broken states (the `aisearch-openai-rag-audio` 13-attempt deadlock was exactly this). New `AzureModelCatalogProbe` (singleton, 24 h per-region cache) shells out to `az cognitiveservices model list -l <region> -o json` — same CLI and same `DefaultAzureCredential` already used by every other component — and renders the result as a compact `AZURE OPENAI MODELS AVAILABLE IN '<region>'` block listing every `(model name, version, sku)` tuple that's actually deployable. Both the in-process `EscalationResolverAgent` and the Foundry-hosted `FoundryEscalationResolverClient` accept an optional `azureRegion` parameter and embed the catalog block in the JSON envelope they send to the LLM. The system prompt has a new authoritative-grounding rule: "BOTH the new name AND the new version MUST appear together in that catalog. NEVER propose a (name, version) that is not listed there." Net effect: the resolver stops guessing version dates from training-data folklore and grounds every model swap on a live snapshot — the same surface a human operator would consult before editing a Bicep file. Failure modes are non-fatal (no `az`, auth missing, region unknown → empty block, resolver runs un-grounded as before). Wired into `DeploymentOrchestrator` at both resolver call sites; `s.AzureLocation` is the region passed in.
- **Auto-patch Pattern C: ARM `Format:OpenAI,Name:X,Version:Y` extractor + missing `gpt-4o-realtime-preview` version entry** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). Pattern C used to deadlock the `aisearch-openai-rag-audio` sample in a 13-attempt ping-pong. The first ARM error is `ServiceModelDeprecated: ... 'Format:OpenAI,Name:gpt-4o-realtime-preview,Version:2024-12-17' has been deprecated`. The old quoted-token regex captured the name `gpt-4o-realtime-preview` (and the `version 2024-12-17` regex captured the date), but the version table had no `("gpt-4o-realtime-preview","2024-12-17")` key, so only the model was swapped to `gpt-realtime` while the version stayed `2024-12-17`. The next validation then failed with `DeploymentModelNotSupported: ... 'Format:OpenAI,Name:gpt-realtime,Version:2024-12-17'`, where the colons in the ARM-quoted blob defeated the `[A-Za-z0-9\-_.]` quoted-token regex entirely — Pattern C bailed out and the Foundry-hosted Doctor entered an infinite revert loop swapping the name back and forth without ever touching the version. Two fixes: (1) added an explicit ARM regex `Name:\s*<model>\s*,\s*Version:\s*<YYYY-MM-DD>` probed *before* the quoted-token fallback, so the canonical ARM error always yields both halves of the (model, version) pair authoritative; (2) added `("gpt-4o-realtime-preview","2024-12-17") => "2025-08-28"` (and the mini variants) to the version table so the single auto-patch pass swaps both name and version atomically. Net effect: on the first Doctor escalation the synthesised step now performs `gpt-4o-realtime-preview → gpt-realtime` AND `2024-12-17 → 2025-08-28` in one pass, and the deploy converges on the next `azd up`.
- **Auto-patch Pattern D: `InvalidResourceLocation` cross-region collision** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). When a repo has been deployed once in region X (creating an RG and region-pinned resources like a managed identity / ACR / Search) and a new run targets a different region Y, ARM rejects the deployment with `InvalidResourceLocation: The resource '<name>' already exists in location '<X>' in resource group '<rg>'. A resource with the same name cannot be created in location '<Y>'.` because `azd` derives RG and resource names deterministically from `AZURE_ENV_NAME`. New deterministic pattern in `TryAutoPatchEscalation`: regex extract the pre-existing region from `already exists in location '<region>'`, emit a `replace_step` Remediation that runs `bash -lc 'cd /workspace; azd env set AZURE_LOCATION <region>; agentic-azd-up'` so the current attempt converges on the partially-provisioned RG instead of fighting ARM over name collisions. Non-destructive (does not delete the old RG — that would require explicit human consent). Triggered by `Azure-Samples/aisearch-openai-rag-audio` Step 18 escalating with the combined ARM signature after a previous run had already created `rg-aisearch-openai-rag-audio-…` in eastus2 and the new attempt targeted westus2.
- **Foundry-hosted variant of the EscalationResolver** ([`AgentStationHub/Services/Tools/FoundryEscalationResolverClient.cs`](AgentStationHub/Services/Tools/FoundryEscalationResolverClient.cs)). The in-process `EscalationResolverAgent` (Meta-Doctor, Chat Completions on `DoctorDeployment`) now has a hosted twin that talks to a Foundry agent (e.g. `AgentEscalationResolver`) over the same **Responses API** transport used by Agent Learn (`FoundryAgentChatClient`). When the feature flag `Foundry:UseFoundryEscalationResolver=true` is set AND `Foundry:EscalationResolver:ProjectEndpoint` is configured, the orchestrator prefers the hosted client and the in-process agent is bypassed; when off (default), the in-process agent runs as before. Activation: set on the VM `.env`: `FOUNDRY_USE_FOUNDRY_ESCALATION_RESOLVER=true` + optional `FOUNDRY_ESCALATION_RESOLVER_PROJECT_ENDPOINT=https://<account>.services.ai.azure.com/api/projects/<proj>` + `FOUNDRY_ESCALATION_RESOLVER_AGENT_NAME=AgentEscalationResolver`. The hosted agent must be authored in the Foundry portal (kind=prompt, model=any JSON-mode-capable e.g. `gpt-4.1-mini` or `o4-mini`, output schema = `{kind: replace_step|insert_before|give_up, command, rationale, confidence}`) and the principal that runs the Hub must hold `Azure AI User` on the project scope. Both the in-process and the hosted client run their output through `PlanValidator` and the same dedup-against-`previousAttempts` heuristics, so security/correctness invariants are identical regardless of where the resolver lives. Why hosted: instructions and model can be edited from the Foundry portal without redeploying the Hub; the agent can also be wired to MCP/OpenAPI tools (e.g. an Azure Resource Graph tool) for grounded fixes. Why keep in-process as default: zero extra moving parts on bootstrap; instant start.
- **`EscalationResolverAgent` (Meta-Doctor): LLM-driven last-line resolver** ([`AgentStationHub/Services/Agents/EscalationResolverAgent.cs`](AgentStationHub/Services/Agents/EscalationResolverAgent.cs)). The first three iterations of self-healing on `[Escalate]` verdicts shipped as a hand-coded regex table inside `TryAutoPatchEscalation` (Patterns A/B/C: `ContainerAppSecretInvalid`, `InvalidPrincipalId`, deprecated/unsupported AOAI model). That table grows by hand every time the user encounters a new failure signature and copy-pastes it. The new `EscalationResolverAgent` closes the loop: when the Doctor escalates AND none of the deterministic patterns match, the orchestrator now calls a chat-completions agent (Azure OpenAI `DoctorDeployment`, JSON-only response format) with the failing command, log tail, Doctor reasoning, and `previousAttempts`. The agent returns a strict JSON `{kind, command, rationale, confidence}`; output is validated against `PlanValidator` (allow-list of binaries, blacklist of destructive shell, multi-level-quoting check) and against the in-session `previousAttempts` (so the same fix is never re-applied). On success the synthesised step is applied with the correct semantics: `replace_step` swaps the failing step in place; `insert_before` keeps it and runs the fix first. The hard-coded patterns remain as a free fast-path. Net effect: when a brand-new ARM error or azd hook failure shows up in the log, the operator no longer needs to copy-paste it back into the Hub repo and add another `if` block — the agent absorbs the long tail. Triggered by the third self-healing iteration on `Azure/intelligent-app-workshop` (Step 6 sub-deployment failed with `ContainerAppSecretInvalid` + `InvalidPrincipalId` because the Strategist chose Bicep-direct over `azd up` for a sample whose `azure.yaml` lives in `workshop/dotnet/`).
- **Apply-block now honors `replace_step` Remediation kind** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). The auto-patch apply block previously only `Insert`ed the new steps before the failing step (insert_before semantics), even when `TryAutoPatchEscalation` returned `Kind: "replace_step"` (Patterns A & B for `ContainerAppSecretInvalid` / `InvalidPrincipalId`). Side effect: the original failing step would still execute right after the patch and re-fail identically. The apply block now branches on `autoPatch.Kind` — `replace_step` overwrites `steps[i]` and inserts any extra steps after it; `insert_before` keeps current behaviour. Same semantics applied to `EscalationResolverAgent` output.
- **Orchestrator: auto-patch override for `ContainerAppSecretInvalid` + `InvalidPrincipalId`** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). Two new deterministic patterns in `TryAutoPatchEscalation` for ARM sub-deployments: (A) `ContainerAppSecretInvalid` — when the failing `az deployment …` carries `name=''` or `name=""` parameters (ACA rejects empty secret values), the helper strips those tokens via regex and emits a `replace_step` Remediation with the rewritten command (Bicep `@secure() string foo = ''` defaults cover the dropped parameters); (B) `InvalidPrincipalId` — when the failing `az deployment …` lacks a `principalId=` parameter, the helper wraps the original command in `bash -lc 'PID=$(az ad signed-in-user show --query id -o tsv \|\| az ad sp show --id "$AZURE_CLIENT_ID" --query id -o tsv); … <cmd> principalId=$PID'`. Both return `Kind: "replace_step"`. Triggered by `Azure/intelligent-app-workshop` Step 6 escalating with the combined ARM signature.

 ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). The first iteration of `TryAutoPatchEscalation` only matched on the trigger word `deprecated` and only swapped the model NAME — so when the Doctor escalated a second time on `aisearch-openai-rag-audio` Step 13 with "the bicep template references an **unsupported** OpenAI model 'gpt-realtime' **version '2024-12-17'** in eastus2", the helper skipped (wrong trigger word + the model-name table didn't list `gpt-realtime`) and the session ended `BlockedNeedsHumanOrSourceFix` again. The helper now (1) accepts `deprecated|unsupported|not supported|retired|no longer available` as triggers, (2) iterates every quoted token in the reasoning and picks the FIRST one that looks like an OpenAI model id, (3) extracts an optional `version 'YYYY-MM-DD'` from the same reasoning, (4) applies a (model, badVersion) → goodVersion table — initial entries `("gpt-realtime","2024-12-17") → "2025-08-28"`, `("gpt-realtime","2024-10-01") → "2025-08-28"`, `("gpt-4o","2024-05-13"|"2024-08-06") → "2024-11-20"` — and (5) emits a single `bash -lc '…'` step that does both rewrites in one pass. The version sed is restricted to lines containing `version` (`sed -i -E "/version/I s|<old>|<new>|g"`) so unrelated dates in the repo are not clobbered. Together with the model-name swap from the previous iteration, the Doctor can now escalate twice in a row on the same repo and the orchestrator will mechanically resolve both — `gpt-4o-realtime-preview` → `gpt-realtime`, then `gpt-realtime` version `2024-12-17` → `2025-08-28` — before the deploy advances. Triggered by `Azure-Samples/aisearch-openai-rag-audio` second escalation after the first auto-patch had landed.
- **Orchestrator: auto-patch override for `[Escalate]` verdicts on deprecated OpenAI models** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). When the (hosted Foundry or in-sandbox) Doctor returns `give_up [Escalate]` with reasoning that names a deprecated OpenAI model (`gpt-4o-realtime-preview`, `gpt-4-turbo-preview`, `gpt-35-turbo`, …), the orchestrator no longer treats the session as `BlockedNeedsHumanOrSourceFix`. A new `TryAutoPatchEscalation` helper extracts the deprecated model name from the Doctor's reasoning + step tail (regex over `deprecated…'name'` and `'name'…deprecated`), maps it to a supported replacement (e.g. `gpt-4o-realtime-preview` -> `gpt-realtime`, `gpt-4-turbo-*` -> `gpt-4o-mini`), and synthesises a `Remediation { Kind: "insert_before" }` with a single bash step that `grep -rl … | xargs sed -i` the substitution across `*.bicep` / `*.bicepparam` / `*.json` / `*.yaml` / `*.yml` / `*.env` files in `/workspace`. The synthesised step is logged as `🩺 Doctor escalated, but the orchestrator recognised the failure signature as auto-patchable…`, recorded in `previousAttempts` as `AUTO_PATCH:` so a re-failure surfaces it next round, and the failing step is re-run inline. Rationale: the Doctor's job is to produce a remediation; when its hosted backend escalates a mechanical fix, the orchestrator fills the gap deterministically rather than bouncing the user out to "open a PR upstream". Triggered by `Azure-Samples/aisearch-openai-rag-audio` Step 12 escalating with "infra/main.bicep is deploying the deprecated OpenAI model 'gpt-4o-realtime-preview'; please update the Bicep to use a supported model name or version" — exactly the signature the new helper handles.
- **Sandbox v34: `agentic-azd-up` self-heals corrupted `azd` `.env`** ([`AgentStationHub/Services/Tools/SandboxImageBuilder.cs`](AgentStationHub/Services/Tools/SandboxImageBuilder.cs)). When any earlier step injects a malformed line into `.azure/<env>/.env` (e.g. an unquoted JSON value, a key containing `{`, or an embedded newline), every subsequent `azd env get-value` / `azd provision` / `azd deploy` call fails with `loading .env: unexpected character "{" in variable name near "{=\"{\"\n"` and the deploy is wedged at the AZURE_LOCATION precheck. The helper now runs `_sanitize_azd_env` BEFORE any azd call: walks `.azure/*/.env`, validates each line's key against the `^[A-Za-z_][A-Za-z0-9_]*$` shell-identifier regex, drops lines that don't match (saving a `.bak.<ts>` copy for forensics), and only then chains into `agentic-azd-env-prime`. Triggered by `Azure-Samples/get-started-with-ai-agents` Step 10 failing with `agentic-azd-up: AZURE_LOCATION missing` because azd refused to load a `.env` whose first malformed line was `{="{"`.
- **Sandbox v33: `agentic-azd-up` resolves Container Apps via `azd-service-name` tag** ([`AgentStationHub/Services/Tools/SandboxImageBuilder.cs`](AgentStationHub/Services/Tools/SandboxImageBuilder.cs)). v32 resolved each service's Container App by matching `name=='$svc'`, `starts_with`, or `contains`. That fails on samples like [`Azure-Samples/get-started-with-ai-agents`](https://github.com/Azure-Samples/get-started-with-ai-agents) where `azure.yaml` declares `services.api_and_frontend` but `infra/api.bicep` names the CA `ca-api-<uniqueString>`; the linkage is the Bicep tag `tags.azd-service-name='api_and_frontend'` (which is what azd itself uses). The helper now queries `az containerapp list --query "[?tags.\"azd-service-name\"=='$svc'].name | [0]"` FIRST, falls back to the name-match strategies, and finally tries the `_`→`-` sanitised variant before giving up. Triggered by `get-started-with-ai-agents` Step 6 reporting `cannot resolve Container App name in rg-get-started-with-ai-agent-...` after the ACR remote build had succeeded — the Doctor's `[Escalate]` blaming the source repo was a false positive (the repo's `azd-service-name` tag is correctly set; the bug was in our helper).
- **Orchestrator: scope the `azd provision`-without-`azd deploy` rejection to azd-deploy steps only** ([`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). The `DegradesDeploy` guard was blanket-rejecting any Doctor remediation whose new step contained `azd provision` without a paired `azd deploy`, regardless of which step was being replaced. That correctly blocks "replace `azd up` with `azd provision`" (which leaves Container Apps on the `hello-world` placeholder), but it ALSO blocked legitimate prereq fixes — e.g. when an earlier `az acr create` step fails because `${AZURE_RESOURCE_GROUP}` is empty (azd hasn't provisioned yet), the Doctor's correct fix is "`azd provision` first, then query the real RG via `azd env get`, then re-run `az acr create`". Rule 1 now fires only when the step being REPLACED is itself an `azd up` / `azd deploy` step; for any other step the Doctor is allowed to fold `azd provision` in as a prerequisite. Rule 2 (`*_RESOURCE_EXISTS=true` skipping) is unchanged. Triggered by `Agentic-AI-Investment-Analysis-Sample` Step 17 deadlocking after Doctor attempt #11 was rejected for replacing an ACR-creation step (not an `azd up`) with `azd provision && az acr create ...`.
- **Robustness pass: pre-flight CommandSafetyGuard + fragile-wait silence cap** ([`AgentStationHub/Services/Security/CommandSafetyGuard.cs`](AgentStationHub/Services/Security/CommandSafetyGuard.cs), [`AgentStationHub/Services/Security/PlanValidator.cs`](AgentStationHub/Services/Security/PlanValidator.cs), [`AgentStationHub/Services/DeploymentOrchestrator.cs`](AgentStationHub/Services/DeploymentOrchestrator.cs)). Shifted from per-fail-mode prompt rules to a **deterministic correctness guard** running on BOTH the initial Strategist plan AND every Doctor remediation, before the step ever reaches the sandbox. Hard rules (block + tag in PREVIOUS_ATTEMPTS so the Doctor pivots): (1) `az acr wait` — not a valid subcommand; (2) `az resource wait --created --name <hardcoded-short-literal>` — almost always wrong because Bicep templates suffix names with a uniqueString hash; the literal will never exist and the command blocks for up to 60 min; (3) `node <name>.js` wrapping a baked shell helper (`relocate-node-modules`, `agentic-*`); (4) `| sh` / `| bash` pipelines (already a security-validator hit, surfaced earlier with a friendlier reason). Soft rule: `<short>.azurecr.io` literals get a warning visible to the next Doctor pass. Plus a runtime safety net independent of the static guard: any `az (resource|group|deployment) wait --(created|exists|deleted)` step has its silence budget capped at **4 minutes** (vs the default 15 / heavy 60), so a dynamic-substitution wrong-name wait can no longer wedge the entire deploy for an hour. Triggered by `Agentic-AI-Investment-Analysis-Sample` Step 5 hanging on `az resource wait --name aiinvest --created` (the real ACR was `aiinvestacrtnggmxliptxjw`); the durable fix is structural — stop relying on prompt prose to enforce correctness invariants.
- **Doctor: ACR "Could not connect to login server" — query the real registry name, never `az resource wait` a guessed one** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). When the build/push step fails with `Could not connect to the registry login server '<name>.azurecr.io'`, the Strategist had hardcoded the registry name from `namePrefix` (e.g. `aiinvest`) — but most Bicep templates suffix `namePrefix` with a deterministic random/`uniqueString` token at deploy time (real name: `aiinvestacrtnggmxliptxjw`). The Doctor previously fell back to `az acr wait --name aiinvest` (subcommand does not exist) and then `az resource wait --name aiinvest --created` — which **blocks for up to 60 minutes** waiting for a resource that will never be created, wedging the entire deploy. New known-failure rule MANDATES `kind="insert_before"` with a step that resolves the actual registry via `az acr list -g <rg> --query "[0].loginServer" -o tsv`, exports it to `/workspace/.acr-env`, and a follow-up `replace_step` for the build/push that does `. /workspace/.acr-env && bash infra/2-build-and-push-images.sh -r "$ACR_LOGIN_SERVER"`. `az acr wait` and `az resource wait --created` on a guessed name are explicitly forbidden. Triggered by `Agentic-AI-Investment-Analysis-Sample` Step 5 hanging indefinitely on `az resource wait --name aiinvest --created` after the namePrefix-only ACR name 404'd at login.
- **Doctor: Cosmos DB "failed provisioning state" recovery — delete the carcass first** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). When `az deployment group create` returns `BadRequest: The DatabaseAccount '<name>' is in a failed provisioning state. Please delete the previous instance and retry`, the Doctor now emits an `insert_before` step that lists every Cosmos DB account with `provisioningState=='Failed'` in the target RG and deletes them via `az cosmosdb delete -g <rg> -n <name> --yes` before retrying the failing deploy verbatim. Without this, the retry hits the same BadRequest forever because the failed account ghost is still tying up the unique name. Triggered after the zone-redundant sed fix unblocked the bicep but a previous (zone-redundant) Cosmos creation had left a `Failed`-state carcass in the RG.
- **Doctor: Cosmos zone-redundant fix MUST be `insert_before`, not folded into `replace_step`** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). The previous Cosmos zone-redundant rule said "emit a sed prep step" but didn't pin the `Remediation.kind`. The hosted Doctor was choosing `replace_step` and rewriting the failing composite step (`bash -c 'delete UAMIs && deploy infra'`) into another `bash -c '...'` chain that did NOT include the `find ... sed -i ...zoneRedundant: false` substitution — so the retry hit the same `ServiceUnavailable: high demand ... zonal redundant ... Availability Zones` error on Cosmos DB. The rule now MANDATES `kind="insert_before"` with a NEW step whose command is exactly the find+sed, leaving the failing step's command unchanged so it retries verbatim. Also broadened the trigger tokens to include `"Database account creation failed"`. Triggered by Investment-Analysis-Sample Step 16 ("Delete user-assigned identities, wait for their deletion, then deploy infra") failing with the canonical zonal-redundant 503 again after the prior fix landed but was bypassed by `replace_step`.
- **Two more fail-modes squashed: doubled-`/endpoint` Foundry URL + Strategist wrapping shell helpers in `node …js`** ([`AgentStationHub/Services/Tools/FoundryDoctorClient.cs`](AgentStationHub/Services/Tools/FoundryDoctorClient.cs), [`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). (1) `NormalizeInvokeUrl` now LOOPS the strip block: if the env var is already pre-doubled (`.../endpoint/endpoint/protocols/invocations`) the previous single-pass logic stripped only the trailing `/endpoint/protocols/invocations`, leaving `.../endpoint`, then re-appended `/endpoint/protocols/invocations` for a still-doubled URL. The loop strips until a stable prefix is reached. (2) Strategist NODE.JS PREVENTIVE STEP block now explicitly forbids wrapping `relocate-node-modules` in `node <name>.js`; matching Doctor known-failure recognises `Cannot find module '/workspace/<helper>.js'` MODULE_NOT_FOUND and rewrites the step to drop both the `node` prefix and the `.js` suffix. Triggered by Investment-Analysis-Sample where the Strategist emitted `node relocate-node-modules.js /workspace` (Step 1 failed) and the hosted Doctor 404'd because the env var produced a doubled URL.
- **Strategist + Doctor: feed `<<< y` to interactive deploy scripts** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). Many shipped Azure deploy scripts contain `read -p "Continue? (y/N): "` confirmation prompts combined with `set -e`. In the sandbox there is no TTY, so `read` fails on EOF and `set -e` aborts the script with exit 1 right after printing its banner echos — the user sees no real error. Strategy 1b prose now mandates a here-string answer (`bash infra/1-deploy-azure-infra.sh -g <rg> -l <region> <<< y`) on every shipped `*.sh` deploy step. The Doctor instructions gained a matching known-failure entry: when the script prints its banners then exits with no specific error and source contains `read -p`, emit a `replace_step` with the same `<<< y` suffix. Pipelines (`yes y | bash …`) are explicitly forbidden because the security validator rejects `| bash` patterns. Triggered by `Agentic-AI-Investment-Analysis-Sample`'s `infra/1-deploy-azure-infra.sh` aborting at exit 1 after the `📋 Current Azure subscription` table.
- **Strategist sees deploy-script source; Doctor recognizes positional-arg-vs-Usage failures** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). `BuildStrategistInput` now appends a `SHELL DEPLOY SCRIPTS` block containing the first ~2.4 KB of each `*.sh` file under `infra/`, so the Strategist can read the script's `getopts` / `case` / `Usage:` block and emit `bash infra/1-deploy-azure-infra.sh -g <rg> -l <region>` instead of `bash infra/1-deploy-azure-infra.sh "<rg>" "<region>"`. Strategy 1b prose gained a **DEPLOY SCRIPT ARG RULE** explicitly forbidding positional args. The Doctor instructions gained a known-failure entry: when the error tail contains `Unknown option <value>` plus a printed `Usage: <script>.sh -X ...` block, emit a `replace_step` that re-invokes the script with the documented named flags. Triggered by `Agentic-AI-Investment-Analysis-Sample`'s `infra/1-deploy-azure-infra.sh`, which uses `getopts -g/--resource-group -l/--location` and rejected the Strategist's positional invocation with `Unknown option agentic-invest-ai-sample`.
- **Doctor: Cosmos DB zone-redundant capacity recognition** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). Added a known-failure rule: when `az deployment group create` returns `ServiceUnavailable` with `"high demand in <region> region for the zonal redundant (Availability Zones) accounts"`, the Doctor now sed-flips `isZoneRedundant: true` -> `false` (and the `zoneRedundant:` variant) across `*.bicep` / `*.bicepparam` and retries the same step. Only after that fails does it fall back to a region change. Triggered by `Agentic-AI-Investment-Analysis-Sample` deploying to `eastus` where Cosmos DB had no zonal-redundant capacity at deploy time.
- **`cwd` field is now sanitized before reaching `PlanValidator`** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs)). Both `ParsePlan` paths (initial plan + Doctor remediation) run the LLM-emitted `cwd` through a new `NormalizeCwd` helper that strips well-known sandbox prefixes (`/workspace/`, `/workdir/`, `/repo/`, `/app/`, `/home/agent/workspace/`), collapses bare aliases (`/workspace`, `~`, `/`) to `.`, and replaces anything still rooted, starting with `~`, or containing `..` with `.`. The Strategist prompt also gained an explicit **CWD FORMAT (HARD RULE)** block listing allowed (`.`, `api-app`, `app/frontend`) and forbidden (`/workspace`, `/repo`, `~`, `..`) values. Without this, an absolute `cwd` on Step 1 caused the host to fail-fast with `Working directory must be relative and inside workdir.` and discard the entire plan — wasting the whole Strategist + Reviewer round-trip on a single bad string. Triggered on the second Strategy 1b run of `Agentic-AI-Investment-Analysis-Sample`.
- **Strategist sees the infra tree; forbidden hallucinated parameter files; Foundry Doctor URL guard for bare `/endpoint`** ([`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs`](AgentStationHub.SandboxRunner/Team/PlanningTeam.cs), [`AgentStationHub/Services/Tools/FoundryDoctorClient.cs`](AgentStationHub/Services/Tools/FoundryDoctorClient.cs)). Two follow-up fixes after the first Strategy 1b run on `Azure-Samples/Agentic-AI-Investment-Analysis-Sample`:
  1. The Strategist correctly emitted Bicep-direct, but hallucinated `--parameters @infra/bicep/main.parameters.json` even though that file does not exist (the repo ships only `infra/bicep/main.bicep` + `modules/`). `az deployment group create` rejected the call with `Unable to parse parameter: @infra/bicep/main.parameters.json`. The Strategist input now surfaces the **verbatim infra/ tree** in a dedicated `INFRA TREE` block so the LLM can see what files actually exist; the Strategy 1b prose explicitly forbids `--parameters @<file>` unless that file is in the listed tree, and tells the Strategist to PREFER reproducing shipped deploy scripts (`infra/*.sh`, `deploy.sh`, Makefile target) over hand-rolling `az` commands.
  2. Hosted Foundry Doctor returned 404 because `Foundry__DoctorAgentEndpoint` was set to `.../agents/ash-doctor-hosted/endpoint` and `NormalizeInvokeUrl` was only stripping `/invocations` and `/endpoint/protocols/invocations` — not a bare `/endpoint` — so the canonical suffix was appended on top of the existing one, yielding `/endpoint/endpoint/protocols/invocations`. The normalizer now also strips a trailing `/endpoint`.
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
