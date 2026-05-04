---
description: "Use when adding or modifying AutoPatch entries in the DeploymentOrchestrator. Covers detection patterns, guard clauses, step insertion, and logging conventions."
applyTo: "**/DeploymentOrchestrator.cs"
---
# AutoPatch System

Deterministic remediation patches that fire BEFORE the Doctor agent evaluates a failure.

## Adding a New AutoPatch

```csharp
// Detection: match error signature in stepTail
var myCondition =
    !string.IsNullOrEmpty(stepTail)
    && stepTail.Contains("error text", StringComparison.OrdinalIgnoreCase)
    && !previousAttempts.Any(a => a.Contains("[AutoPatch:my-name]"));

if (myCondition)
{
    // 1. Build remediation command
    var fixCmd = "bash -lc \"...\"";

    // 2. Record attempt (prevents re-fire)
    previousAttempts.Add("[AutoPatch:my-name] description of what was fixed");

    // 3. Log for operator visibility
    await Log(s, "status", "Auto-patch: description...", step.Id);

    // 4. Insert step and retry
    var patchStep = new DeploymentStep(step.Id, "AutoPatch: short title", fixCmd, step.WorkingDirectory);
    steps.Insert(i, patchStep);
    i--;
    continue;
}
```

## Key Rules

- **Detection**: Use `stepTail.Contains(...)` with `StringComparison.OrdinalIgnoreCase`.
- **Guard**: Always check `!previousAttempts.Any(a => a.Contains("[AutoPatch:name]"))`.
- **ErrorSignatures**: For long-output commands (azd provision), critical errors may only appear in `result.ErrorSignatures` which are appended to `stepTail` automatically.
- **Priority**: When multiple patches could match the same error, use `cosmosRegionWillFireFirst`-style guards to yield to the more comprehensive patch.
- **idempotency**: Patches should be safe to re-run. Use `set +e`, `|| true`, existence checks.
- **Timeout**: For remediation commands that call Azure APIs, use `TimeSpan.FromMinutes(10)` or similar.
