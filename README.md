# AgentStationHub

A Blazor Server (.NET 8) application — hosted on a dedicated Azure VM — that ties together three operator surfaces:

1. **Hub / Agentic Deploy** — paste a GitHub repo URL, an AI agent team builds a deployment plan inside a Docker sandbox, you approve it, the orchestrator executes it end-to-end against your Azure subscription with live logs streamed via SignalR.
2. **Agent Learn** — a floating MS-logo avatar opens an in-app chat that recommends Microsoft Learn modules, paths, certifications, exams and instructor-led courses, powered by Azure OpenAI `gpt-4.1-mini` + a Logic App tool that proxies the live Microsoft Learn catalog.
3. **GitHub Copilot CLI** — a floating launcher pill embeds a real terminal (ttyd) attached to a long-lived sidecar container, reverse-proxied through the Hub's port 8080.

Validated end-to-end (Apr 2026): [`Azure-Samples/azure-ai-travel-agents`](https://github.com/Azure-Samples/azure-ai-travel-agents) (8 services: 3 APIs + 4 MCP servers + UI) deploys to a fresh Azure resource group with **8/8 Container Apps live, 0 failed**, fully autonomously from URL → approve → green. Sandbox image **v32**.

---

## Hosting model

The Hub runs **only on an Azure VM** that you provision once with [`start.ps1`](start.ps1) / [`start.sh`](start.sh). No laptop Docker Desktop, no nested containers on a developer machine.

```
Operator browser ──HTTPS──► Azure VM (agentichub-host, Standard_D2as_v6, Ubuntu)
                            │
                            ├── docker compose up
                            │     ├── agentichub-app   (Blazor :8080)
                            │     └── agentichub-copilot-cli (ttyd sidecar)
                            │
                            └── Sibling sandbox containers spawned per deploy
                                  via /var/run/docker.sock (DooD)
```

- VM lives in resource group `rg-agentichub-host` in `eastus2`. NSG opens 22 + 8080 to your **current public IP only** (refreshed on every `start.ps1` run via `api.ipify.org`).
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
│   ├── Pages/Hub.razor                        Agentic Deploy entry (region picker + repo URL + samples)
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
        └── FoundryAgentChatClient.cs          Agent Learn: AOAI chat-completions + function calling

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

The Hub page (`/hub`) lists curated samples and a free-form repo URL input + Azure region dropdown. Clicking **Agentic Deploy** drives `DeploymentOrchestrator` through seven phases. Live logs and the plan checklist stream into [`DeploymentModal.razor`](AgentStationHub/Components/DeploymentModal.razor) over SignalR (`/hubs/deployment`).

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
| Agent Learn chat | Chat Completions | `gpt-4.1-mini-1` | `Foundry:ChatAgent:Deployment` |

---

## Agent Learn (in-app chat)

Floating circular Microsoft-logo avatar pinned bottom-left of every page (registered in [`MainLayout.razor`](AgentStationHub/Components/Layout/MainLayout.razor)). Clicking it opens a 360×520 chat panel with header *Agent Learn* + a `?` tooltip. The agent recommends Microsoft Learn modules, paths, certifications, exams and instructor-led courses in fluent natural language with Markdown citations to real `learn.microsoft.com` URLs.

```
[User] ──► [Blazor Server: AgentChatPanel]
                   │  per-circuit thread state (system + user/assistant/tool history)
                   ▼
          [FoundryAgentChatClient]
                   │  POST /openai/deployments/gpt-4.1-mini-1/chat/completions?api-version=2024-10-21
                   │  tool: get_learning_content (function call, no parameters)
                   ▼
          [Azure OpenAI on AgenticStationFoundry, kind=AIServices]
                   │  emits tool_call when the user asks for training material
                   ▼
          [FoundryAgentChatClient.CallLearnAsync]
                   │  POST https://logicapp-090730.azurewebsites.net/api/Get_Learning_Content/
                   │       triggers/Request/invoke?api-version=...&sig=... (anonymous SAS)
                   ▼
          [Logic App Standard `logicapp-090730` (Sweden Central)]
                   │  workflow Get_Learning_Content → Microsoft Learn Catalog API
                   ▼
          [learn.microsoft.com Catalog API] → ~14 MB JSON
                   │  truncated to ~120 KB before being passed back to the model
                   ▼
          [gpt-4.1-mini-1] → natural-language answer with Markdown citations
                   ▼
          [User]
```

- **Auth**: `DefaultAzureCredential` inside the Hub container fetches a token for `https://cognitiveservices.azure.com/.default`. The principal in use needs role `Cognitive Services OpenAI User` on the AOAI account (`AgenticStationFoundry`). On the VM both the system-assigned MI and the `ash-doctor-orchestrator` SP (used when `AZURE_CLIENT_ID/SECRET` are set in `.env`) have been granted this role.
- **Logic App auth**: SAS query parameters are baked into `Foundry:ChatAgent:LearnToolUrl`. Easy Auth excludes `/api/*` so the SAS is the only check on the trigger; the rest of the site requires AAD.
- **Why bypass Foundry V2 Responses**: the portal-built agent (`AgentMicrosoftLearn`) exposes a Responses-protocol URL of the form `…/api/projects/default/agents/<id>/protocols/openai/v1/responses?api-version=…`, but in `swedencentral` the runtime returned 404 for every probe. The in-process AOAI + function-calling implementation ships the same UX today.
- **Catalog truncation**: tool-result string is truncated to 120 KB before being added to the conversation history (raw catalog is ~14 MB). The model still sees plenty of items in the head of each array (`modules`, `learningPaths`, `certifications`, `exams`, `courses`).
- **System prompt** (in [`FoundryAgentChatClient.SystemPrompt`](AgentStationHub/Services/Tools/FoundryAgentChatClient.cs)) enforces natural-language replies in the user's language, 3–6 recommendations woven into prose, Markdown links on titles, no JSON / tool-mention leakage.

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

---

## Debug HTTP API

The Hub's port 8080 is `127.0.0.1`-only on the VM. Three unauthenticated debug endpoints help autopilot scripts drive deploys without the Blazor UI:

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

- **Agent Learn — floating chat avatar in the Hub sidebar** ([`AgentChatPanel.razor`](AgentStationHub/Components/AgentChatPanel.razor), [`FoundryAgentChatClient.cs`](AgentStationHub/Services/Tools/FoundryAgentChatClient.cs), wiring in [`Program.cs`](AgentStationHub/Program.cs)). MS-logo avatar opens a chat powered by Azure OpenAI `gpt-4.1-mini-1` with a single function-calling tool (`get_learning_content`) that fetches the live Microsoft Learn catalog through Logic App Standard `logicapp-090730`. Foundry V2 Responses returned 404 on cluster `hyena-swedencentral-02` (preview), so the agent runs in-process via the chat-completions data plane on the AgenticStationFoundry account. `?` tooltip next to the title surfaces the architecture summary inline.
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
