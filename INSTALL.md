# Installation Guide — AgentStationHub

This guide walks you from a cold laptop to a running AgentStationHub instance in a browser tab. It is designed to be **the single document** a new user reads to get started. For product-level docs (what AgentStationHub is, how the deploy pipeline works), see the main [README](./README.md).

---

## TL;DR — one command

If you already have Docker Desktop running and an Azure login:

```powershell
# Windows / macOS / Linux with PowerShell 7+:
pwsh .\start.ps1
```

```bash
# macOS / Linux without PowerShell:
./start.sh
```

Done. The script creates `.env` interactively (3 prompts), builds the image, starts the container, polls readiness, and opens your browser at <http://localhost:8080/hub>.

---

## Prerequisites

| Requirement | Why | How to install |
|---|---|---|
| **Docker Desktop 4.20+** | The app runs in a container and spawns sibling containers for deployment sandboxes. | <https://www.docker.com/products/docker-desktop> |
| **PowerShell 7+** *(recommended)* | `start.ps1` is the canonical launcher. Cross-platform. | <https://aka.ms/PSWindows> · `brew install powershell` · `apt install powershell` |
| **Azure CLI** *(optional but recommended)* | The app reuses your host `az login` so deployed apps authenticate with your user identity. Without it, the in-sandbox flow falls back to device-code login per deploy. | <https://aka.ms/installazurecliwindows> · `brew install azure-cli` |
| **Git** *(optional)* | Only needed to clone this repo. | <https://git-scm.com> |

> **No .NET SDK needed on the host.** The Dockerfile builds everything inside a multi-stage image. Users who just want to run the app can skip installing any .NET tooling entirely.

### Host architecture support

| Host OS | Arch | Tested? |
|---|---|---|
| Windows 10/11 + Docker Desktop (WSL2 backend) | x64 / arm64 | ? primary dev target |
| macOS (Apple Silicon) | arm64 | ? (native linux/arm64 build) |
| macOS (Intel) | x64 | ? |
| Linux (Ubuntu/Debian) | x64 / arm64 | ? |

The Dockerfile uses `$TARGETARCH` to build natively on either arch — no emulation overhead.

---

## Quick start (new laptop)

```powershell
# 1. Install Docker Desktop and wait for the whale icon to stop animating.
# 2. Clone the repo:
git clone https://github.com/<org>/AgentStationHub.git
cd AgentStationHub

# 3. Run the bootstrap launcher:
pwsh .\start.ps1
```

You will see something like:

```
?? Environment ??????????????????????????????????????
  ? All required files present (Dockerfile, docker-compose.yml, …)

?? Docker ?????????????????????????????????????????
  ? docker CLI found
  ? Docker daemon reachable (server 29.4.0)

?? Azure CLI ??????????????????????????????????????
  ? Logged in as alice@example.com (subscription: Sub-01)

?? Configuration (.env) ???????????????????????????
  ! .env missing, let's create it.
    Azure OpenAI endpoint: https://mycog.openai.azure.com/
    Main deployment (blank = gpt-5.4):
    Runner deployment (blank = gpt-5.3-chat):
  ? .env created.

?? Stack ??????????????????????????????????????????
  ? Container started.

?? Readiness ??????????????????????????????????????
  ? App is up at http://localhost:8080/hub
```

Browser opens. Done.

**First run** takes 3-5 minutes (build + first `az login`). **Subsequent runs** are ~5-10 seconds.

---

## What the launcher does (step by step)

### `start.ps1` / `start.sh` pipeline

1. **Environment probe** — detects host OS/arch, validates repo layout (`Dockerfile`, `docker-compose.yml`, `entrypoint.sh`, `.env.example`).
2. **Docker preflight** — `docker version` with 8 s timeout. If the daemon is down:
   - **Windows**: auto-launches Docker Desktop from Program Files or LocalAppData.
   - **macOS**: auto-launches `/Applications/Docker.app`.
   - **Linux**: instructs the user to `sudo systemctl start docker`.
   - Polls up to 90 s for the daemon to come up.
3. **Compose detection** — prefers `docker compose` (plugin, bundled with Desktop 20.10+), falls back to legacy `docker-compose`.
4. **Azure CLI check** — verifies `az` is on PATH and the user has a default subscription (`az account show`). Runs `az login` interactively if missing and `-NonInteractive` was not passed.
5. **`.env` bootstrap** — if missing, prompts for three values with sensible defaults:
   - `AZURE_OPENAI_ENDPOINT` *(required)*
   - `AZURE_OPENAI_DEPLOYMENT` *(default: `gpt-5.4`)*
   - `AZURE_OPENAI_RUNNER_DEPLOYMENT` *(default: `gpt-5.3-chat`)*
6. **`docker compose up --build -d`** — launches the stack detached. With `-NoBuild` the build step is skipped (faster warm re-launches).
7. **Readiness probe** — polls `http://localhost:8080/hub` every 3 s for up to 120 s. Times out with exit code 2 and a hint to check `docker compose logs`.
8. **Browser open** — uses `Start-Process` / `open` / `xdg-open` depending on host. Suppressed with `-NoBrowser`.

### Launcher switches

```powershell
pwsh .\start.ps1                   # primary: full interactive setup
pwsh .\start.ps1 -NoBuild          # warm re-launch (skip image rebuild)
pwsh .\start.ps1 -Reset            # down + up (preserves volumes)
pwsh .\start.ps1 -Reset -Purge     # down + remove volumes (wipes agent memory)
pwsh .\start.ps1 -NoBrowser        # headless / server
pwsh .\start.ps1 -NonInteractive   # CI: fail instead of prompting
pwsh .\start.ps1 -Port 9090        # check a different port
```

### `start.sh` flags

The bash script supports the happy path only: build + up + wait + open. For more, use the PowerShell version or run `docker compose` directly.

---

## Manual install (no launcher script)

If you prefer to do it yourself:

```powershell
# 1. Copy the env template and edit it:
Copy-Item .env.example .env
notepad .env          # fill in AZURE_OPENAI_ENDPOINT + deployment names

# 2. (Optional) Log in to Azure on the host — the container reuses this:
az login

# 3. Build + start the stack:
docker compose up --build -d

# 4. Open the browser:
Start-Process http://localhost:8080/hub

# 5. Tail logs:
docker compose logs -f agentichub

# 6. Stop:
docker compose stop
```

All subsequent launches become `docker compose up -d` + browser open.

---

## Configuration reference (`.env`)

```ini
# ---- Azure OpenAI (required) ----
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_DEPLOYMENT=gpt-5.4
AZURE_OPENAI_RUNNER_DEPLOYMENT=gpt-5.3-chat

# ---- Azure auth (optional, pick ONE) ----
# A) AAD via host's 'az login' (recommended). Pin tenant only when
#    your account is multi-tenant:
# AZURE_TENANT_ID=<tenant-guid>

# B) Fall back to a static API key (dev / CI scenarios):
# AZURE_OPENAI_API_KEY=<key>
```

How `.env` values flow into the app:

| `.env` key | Container env var | App config key |
|---|---|---|
| `AZURE_OPENAI_ENDPOINT` | `AzureOpenAI__Endpoint` | `AzureOpenAI:Endpoint` |
| `AZURE_OPENAI_DEPLOYMENT` | `AzureOpenAI__Deployment` | `AzureOpenAI:Deployment` |
| `AZURE_OPENAI_RUNNER_DEPLOYMENT` | `AzureOpenAI__RunnerDeployment` | `AzureOpenAI:RunnerDeployment` |
| `AZURE_TENANT_ID` | `AzureOpenAI__TenantId` | `AzureOpenAI:TenantId` |
| `AZURE_OPENAI_API_KEY` | `AzureOpenAI__ApiKey` | `AzureOpenAI:ApiKey` |

The double-underscore is .NET's convention for nested config keys — Compose passes `AzureOpenAI__X` which the framework reads as `cfg["AzureOpenAI:X"]`.

---

## How the container setup works

### Why Docker-out-of-Docker (DooD), not Docker-in-Docker

AgentStationHub *itself* spawns sandbox containers for each deploy. The container hosting the app must therefore reach a Docker daemon. Two patterns are possible:

| Pattern | How | Trade-offs |
|---|---|---|
| **DinD** (Docker-in-Docker) | Run a full `dockerd` inside the container (`--privileged`) | Requires privileged mode (security risk), nested daemon downloads images twice, slow, fragile on Docker Desktop. |
| **DooD** (Docker-out-of-Docker) ? | Bind-mount the host's `/var/run/docker.sock` so the in-container `docker` CLI talks to the HOST daemon. Child containers are **siblings**, not nested. | Same security boundary as the host user. Shares image cache. Simpler networking. |

AgentStationHub uses DooD. The `docker` CLI inside the container is wired to the host daemon via the socket bind mount.

### Path-matching bind mounts (the critical detail)

When the app-in-container tells the host Docker daemon `docker run -v /path/X:/workspace`, the **host daemon** resolves `/path/X` on the **HOST filesystem** — not inside the app container. So any path the app generates for sibling mounts must be valid on the host too.

`docker-compose.yml` solves this by bind-mounting two directories at the **same absolute path** on both sides:

```yaml
volumes:
  - /var/agentichub-work:/var/agentichub-work     # scratch for cloned repos
  - /var/agentichub-tools:/var/agentichub-tools   # pre-published SandboxRunner
```

- On **Docker Desktop** (Windows/macOS), these paths live inside the WSL2 / HyperKit Linux VM where the daemon runs — so host and container see the same filesystem at the same path. Docker auto-creates missing directories.
- On **Linux hosts**, `/var/agentichub-*` is a real host directory. The first `compose up` creates it.

A named Docker volume cannot be used here — it lives in Docker-managed storage at a path the daemon cannot reach via a plain `-v` bind target.

### `entrypoint.sh` — runner seeding

The SandboxRunner dll must be accessible to both the app container AND sibling sandbox containers. The image builds it to `/runner-src`, and on first boot `entrypoint.sh` copies it to `/var/agentichub-tools` (the shared bind mount). Subsequent boots skip the copy if the dll is already there.

```sh
if [ ! -f "$TOOLS_DIR/AgentStationHub.SandboxRunner.dll" ]; then
    cp -r /runner-src/. "$TOOLS_DIR/"
fi
exec dotnet AgentStationHub.dll
```

### Shared Azure login

`~/.azure` from the host is bind-mounted read-only at `/root/.azure` inside the container. The app parses `azureProfile.json` directly to pick up your default subscription + tenant, so no login is needed inside the container. The in-sandbox `az` CLI reuses the same tokens through a separate volume (managed by the sandbox auth layer).

### Volume summary

| Mount type | Host path | Container path | Purpose |
|---|---|---|---|
| Socket bind | `/var/run/docker.sock` | `/var/run/docker.sock` | DooD |
| Dir bind | `/var/agentichub-work` | `/var/agentichub-work` | Cloned repos, DooD-visible |
| Dir bind | `/var/agentichub-tools` | `/var/agentichub-tools` | SandboxRunner dll, DooD-visible |
| Dir bind (ro) | `$HOME/.azure` | `/root/.azure` | Shared Azure login |
| Named vol | `agentichub-state` | `/root/.local/share/AgentStationHub` | Agent catalog + memory insights (persistent) |

---

## Troubleshooting

### `docker: Error response from daemon: invalid mount config`

**Cause:** the bind-mount paths don't exist on the host.

**Fix:**

```bash
# Linux / WSL2:
sudo mkdir -p /var/agentichub-work /var/agentichub-tools
sudo chmod 0755 /var/agentichub-work /var/agentichub-tools
docker compose down && docker compose up -d
```

On Docker Desktop the directories are created automatically inside the WSL2 VM — if you see this error, the Docker Desktop service needs a restart.

### Sandbox cannot find `AgentStationHub.SandboxRunner.dll`

**Cause:** the `/var/agentichub-tools` volume is empty (entrypoint copy was skipped when the app was stopped mid-seed).

**Fix:**

```bash
sudo rm -rf /var/agentichub-tools/*
docker compose restart agentichub
# Watch the entrypoint re-seed:
docker compose logs agentichub | grep 'seeding'
```

### `docker version` hangs / "Engine starting" forever on Windows

**Cause:** Docker Desktop's WSL2 distribution is corrupt (see Microsoft [recovery docs](https://docs.docker.com/desktop/troubleshoot/overview/)).

**Fix** (preserves no data; use only when stuck):

```powershell
# 1. Kill all Docker processes:
Get-Process | Where-Object { $_.ProcessName.StartsWith("docker") -or $_.ProcessName.StartsWith("com.docker") } |
    Stop-Process -Force

# 2. Reset WSL:
wsl --shutdown
wsl --unregister docker-desktop-data

# 3. Remove corrupt VHDX:
Remove-Item "$env:LOCALAPPDATA\Docker\wsl\*\*.vhdx" -Force -ErrorAction SilentlyContinue

# 4. Start Docker Desktop — it will recreate the WSL distro:
Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
```

### `az login` asked inside a sandbox at deploy time

**Cause:** host's `~/.azure` was empty or not mounted when the stack started.

**Fix:** run `az login` on the host BEFORE `compose up`:

```powershell
az login
pwsh .\start.ps1 -Reset
```

### Preflight error: "Docker Desktop is not running or not reachable"

The app does a `docker version` check in <500 ms at the start of every deploy. If it fails, the deploy aborts immediately with a clear message. Start Docker Desktop and retry.

### Port 8080 already in use

Pass a different port:

```powershell
pwsh .\start.ps1 -Port 9090
# and edit docker-compose.yml ports: "9090:8080" (the script only changes
# the URL it polls, not the compose file).
```

Or stop whatever is on 8080:

```powershell
Get-NetTCPConnection -LocalPort 8080 | Stop-Process -Force
```

---

## Distributing a pre-built image

Once you have a working build, push to a registry so teammates can skip the `--build` step entirely:

```powershell
# Tag + push (replace with your registry):
docker tag agentichub/app:latest myregistry.azurecr.io/agentichub:v1
docker push myregistry.azurecr.io/agentichub:v1
```

Teammates run with just Docker Desktop + `az login` on their machine:

```bash
docker run --rm -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v /var/agentichub-work:/var/agentichub-work \
  -v /var/agentichub-tools:/var/agentichub-tools \
  -v "$HOME/.azure:/root/.azure:ro" \
  --env-file .env \
  myregistry.azurecr.io/agentichub:v1
```

No repo clone, no .NET SDK, no build. Just the image + `.env` + Docker.

---

## Security considerations

The DooD socket mount gives the container **root-equivalent control** over the host Docker daemon. This is acceptable for a personal developer tool but warrants care in shared setups:

- **Do not expose the container to the public internet** unless you add authentication in front. It has root privileges over every other container on the host.
- **Do not run alongside untrusted containers** on the same daemon.
- For stricter setups consider:
  - A **Docker socket proxy** (e.g. `tecnativa/docker-socket-proxy`) that filters which API endpoints are exposed to AgentStationHub.
  - A **rootless Docker** host, which narrows the blast radius.
  - A **dedicated Docker Desktop** installation just for this tool.

The `~/.azure` bind mount is **read-only** so the container cannot modify your host Azure login state.

---

## Uninstall / reset

### Stop the stack (keeps data)

```powershell
docker compose stop
```

### Full teardown (removes container, keeps volumes)

```powershell
pwsh .\start.ps1 -Reset
# or: docker compose down
```

### Wipe everything (container + agent memory + work dir)

```powershell
pwsh .\start.ps1 -Reset -Purge
# and to also clear the bind-mounted work + tools dirs:
sudo rm -rf /var/agentichub-work /var/agentichub-tools
```

### Remove the image

```powershell
docker rmi agentichub/app:latest
```

---

## Next steps

- Read the [main README](./README.md) for what AgentStationHub actually does (Agent Framework deploy pipeline, sandbox runner, memory insights, …).
- To run the app **natively** (without a container) for development, see the "Getting started" section of the main README.
- To contribute, the two .NET projects are `AgentStationHub/` (Blazor Server UI + orchestrator) and `AgentStationHub.SandboxRunner/` (the in-sandbox planning team).
