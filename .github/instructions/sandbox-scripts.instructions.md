---
description: "Use when editing baked shell scripts in SandboxImageBuilder.cs. Covers string concatenation format, logging patterns, azd helpers, and version bumping."
applyTo: "**/SandboxImageBuilder.cs"
---
# Baked Shell Scripts

Scripts in this file are embedded as C# string concatenation and baked into the sandbox Dockerfile at build time.

## Format Rules

- Every script line is a C# string literal: `"  command here\n" +`
- Maintain consistent indentation within the C# string block.
- Use `\n"` at end of each line (not `\\n`).
- Escape only what C# requires: `\"` for quotes inside strings, `\\` for literal backslash.
- For shell variables: `$var` works as-is in C# strings; only escape `$` if needed for literal dollar sign.

## Script Conventions

- Start scripts with `#!/usr/bin/env bash` and `set +e` for non-fatal flow.
- Use `echo "[script-name] message"` for structured output the orchestrator can parse.
- Use the `_pluck` helper to read azd env values: `_pluck AZURE_RESOURCE_GROUP`.
- Guard destructive operations with `|| true` to prevent cascade failures.
- After any script modification, bump `LocalTag` constant (e.g., `v38` → `v39`).

## Testing

After editing, always:
1. Build: `dotnet build --nologo -v q` (catches unclosed strings, missing `+` operators)
2. Bump LocalTag
3. Commit + deploy to VM to validate in a real sandbox
