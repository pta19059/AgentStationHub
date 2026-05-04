---
description: "Use when working with Docker, docker-compose, deployment to Azure VM, or sandbox container configuration. Covers the DooD pattern, compose services, and VM deploy workflow."
applyTo: ["**/docker-compose.yml", "**/Dockerfile", "**/entrypoint.sh"]
---
# Docker & Deployment

## Architecture

- **DooD (Docker-out-of-Docker)**: The main app mounts `/var/run/docker.sock` and spawns peer sandbox containers — NOT nested Docker.
- **Compose services**: `agentichub-app` (Blazor), `agentichub-caddy` (TLS reverse proxy), `agentichub-copilot-cli` (ttyd sidecar).
- **Sandbox containers**: Ephemeral, spawned per deployment session with `--network host --memory 6g --memory-swap 8g`.

## VM Deploy Workflow

After pushing to master:
```bash
az vm run-command invoke -g rg-agentichub-host -n agentichub-host \
  --command-id RunShellScript \
  --scripts "cd /home/azureuser/agentichub && git checkout -- . && git pull origin master && docker compose build agentichub && docker compose up -d --force-recreate --no-deps agentichub"
```

## Sandbox Image

- Built dynamically by `SandboxImageBuilder.cs` (not a static Dockerfile).
- Base: Azure Linux (Mariner) with azure-cli.
- Includes: azd, Docker CLI, Node.js, Python, .NET SDK, Go.
- Baked scripts: `agentic-azd-up`, `agentic-azd-deploy`, `agentic-azd-env-prime`, `relocate-venv`.
- Version tracked via `LocalTag` constant (bump on any baked script change).

## Compose Guidelines

- Internal services use port 8080 (not exposed to host).
- Caddy handles TLS termination and reverse-proxies to internal ports.
- Persistent state on named volume `agentichub-state`.
- Never expose Docker socket to the internet.
