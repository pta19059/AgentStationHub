# AgentStationHub — Copilot Instructions

## Project Overview

AgentStationHub is a .NET 8 Blazor Server app that orchestrates AI-agent-driven deployments of Azure applications. It spawns ephemeral Docker sandbox containers (Docker-out-of-Docker) where planning and remediation agents run, then streams live results back to the operator via SignalR.

## Architecture

| Component | Purpose |
|-----------|---------|
| **AgentStationHub** (main) | Blazor Server host — UI, orchestrator state machine, Foundry client |
| **AgentStationHub.SandboxRunner** | Console app inside sandbox containers — runs planning/remediation agents |
| **Sandbox containers** | Ephemeral peers spawned via `/var/run/docker.sock` (DooD pattern) |

## Development Workflow

1. **Always build before committing**: `dotnet build --nologo -v q` from the project directory.
2. **Commit + push to `master`** after every meaningful change.
3. **Deploy to VM** after push: `az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "cd /home/azureuser/agentichub && git checkout -- . && git pull origin master && docker compose build agentichub && docker compose up -d --force-recreate --no-deps agentichub"`.
4. **Bump sandbox version** (`LocalTag` in `SandboxImageBuilder.cs`) whenever baked shell scripts change.

## Code Conventions

- **Sealed classes** for all non-inherited services: `public sealed class MyService`.
- **Sealed records** for DTOs and result types: `public sealed record MyResult(int Code, string Message)`.
- **DI registration** in `Program.cs` — Singleton for stateful stores, Scoped for per-request services.
- **IOptions<T>** for typed configuration; environment variables override `appsettings.json`.
- **CliWrap** for Docker/CLI execution; never `Process.Start` directly.
- **Structured logging** via `ILogger<T>`; no `Console.WriteLine`.

## Baked Shell Scripts (`SandboxImageBuilder.cs`)

Shell scripts are embedded as C# string concatenation (heredoc-style) and baked into the sandbox Docker image at build time. When editing these:

- Each line is a C# string literal ending with `\n"` — maintain this pattern.
- Use `echo "[script-name] message"` for structured logging that the orchestrator parses.
- Always `set +e` or `|| true` for commands that may fail non-fatally.
- The `_pluck` helper reads azd env values; prefer it over raw `grep`.
- After modifying any baked script, bump `LocalTag` (e.g. `v38` → `v39`).

## AutoPatch System (`DeploymentOrchestrator.cs`)

The orchestrator has deterministic auto-patches that fire BEFORE the Doctor agent. When adding new patches:

- Detection uses `stepTail.Contains(...)` with `StringComparison.OrdinalIgnoreCase`.
- Guard with `!previousAttempts.Any(a => a.Contains("[AutoPatch:my-name]"))` to prevent re-firing.
- Log the patch via `previousAttempts.Add("[AutoPatch:my-name] description")`.
- Insert the remediation step at position `i` and `i--; continue;` to execute it next.
- `ErrorSignatures` on `DockerShellResult` captures critical patterns regardless of tail position.

## Security

- No secrets in source. Use environment variables or Azure Key Vault.
- `DefaultAzureCredential` for all Azure SDK calls (supports managed identity + CLI fallback).
- Cookie-based simple auth for the operator UI (not public-facing).
- Sandbox containers run with `--network host` but capped memory/swap.

## Testing & Validation

- Build verification: `dotnet build --nologo -v q` must show 0 errors.
- No unit test framework currently — validation is via live deploy sessions.
- The Verifier agent performs post-deploy checks (Container App health, FQDN reachability).

## File Organization

| Path | Contains |
|------|----------|
| `Services/DeploymentOrchestrator.cs` | Core state machine + AutoPatch logic |
| `Services/Tools/SandboxImageBuilder.cs` | Baked scripts + Dockerfile generation |
| `Services/Tools/DockerShellTool.cs` | Per-step exec driver with tail + error capture |
| `Services/Tools/SandboxSession.cs` | Container lifecycle management |
| `Components/Pages/Hub.razor` | Main deployment catalog UI |
| `Hubs/DeploymentHub.cs` | SignalR streaming hub |
| `Models/` | DTOs (no EF, no database) |
