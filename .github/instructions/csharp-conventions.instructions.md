---
description: "Use when creating or modifying C# services, models, DTOs, or DI registrations in AgentStationHub. Covers sealed types, DI patterns, configuration, and logging."
applyTo: ["**/Services/**/*.cs", "**/Models/**/*.cs", "**/Program.cs"]
---
# C# Conventions

## Type Declarations

- **Services**: `public sealed class MyService` — sealed unless designed for inheritance.
- **DTOs / Results**: `public sealed record MyDto(string Name, int Value)` — immutable, positional.
- **Init-only extensions**: Use `{ get; init; }` properties for optional fields on records.

## Dependency Injection

- Register in `Program.cs` with appropriate lifetime:
  - `Singleton`: Stateful stores, caches, long-lived clients (`AgentMemoryStore`, `DeploymentSessionStore`)
  - `Scoped`: Per-request/per-circuit services
  - `Transient`: Stateless utilities (rare)
- Use `IOptions<T>` for configuration sections; never inject `IConfiguration` directly into services.

## Configuration

- Secrets → environment variables (never in `appsettings.json`).
- Non-secrets → `appsettings.json` with `IOptions<T>` binding.
- Environment variable names override JSON keys following ASP.NET conventions (`Section__Key`).

## Error Handling

- Use structured logging: `_log.LogWarning(ex, "Context message with {Parameter}", value)`.
- No `Console.WriteLine` — always `ILogger<T>`.
- Swallow non-fatal exceptions with `_log.LogDebug(ex, "...")` when failure is acceptable.
- Never throw from background tasks without logging first.

## CLI Execution

- Use `CliWrap` for all subprocess calls — never `System.Diagnostics.Process`.
- Prefer `DockerShellTool` for sandbox execution (handles tail capture, silence detection, error signatures).
