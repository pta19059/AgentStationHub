# AgentStationHub Doctor � Foundry Hosted Agent

This is the **ash-doctor** remediation agent extracted from the in-sandbox
`AgentStationHub.SandboxRunner` and packaged as a [Foundry Hosted Agent][1].
When the AgentStationHub host has `AzureOpenAI:UseFoundryDoctor=true`, the
remediation step calls this agent over HTTPS instead of spawning the local
runner. The local runner remains as a fallback.

## Why migrate the Doctor first

Of the three reasoning agents (Strategist, Doctor, Verifier), the Doctor:

- benefits the most from Foundry's eval / prompt-optimizer surface (its
  prompt is the single biggest in the system, ~1100 lines of curated
  failure-signature heuristics);
- has the smallest call-site surface: one method in `SandboxRunnerHost`
  invokes it, vs the Strategist which is wired into the orchestrator
  state machine;
- degrades gracefully when offline because the orchestrator can fall
  back to the in-sandbox runner.

## v0 limitations � important

The hosted Doctor in this version **does not have inspection tools**.
The in-sandbox Doctor uses `read_workspace_file`, `list_workspace_directory`,
`run_diagnostic`, `check_tool_available` to iteratively explore the repo
before proposing a fix. The hosted variant runs in a Foundry container with
no access to the orchestrator's `/var/agentichub-work` volume, so:

- the orchestrator pre-reads files it thinks the Doctor will need and
  ships them in `DoctorRequest.RepoFiles`;
- the hosted Doctor has to reason from that pre-bundled context only.

This is acceptable for v0 (typical 70-80% of cases). A future iteration
will add an HTTP callback channel so the hosted Doctor can request
additional files on demand from the orchestrator.

The condensed v0 prompt in [DoctorBrain.cs](DoctorBrain.cs) is also a
deliberate stub � the full ~1100-line prompt currently lives in
`AgentStationHub.SandboxRunner/Team/PlanningTeam.cs::DoctorInstructions`.
Once the Foundry pipeline is verified end-to-end, the next iteration will
extract that constant to a shared file linked into both projects so a
single source of truth survives.

## Layout

| File | Role |
|------|------|
| `Program.cs` | AgentHost + Invocations server registration. |
| `Contracts.cs` | DTOs (must mirror `RunnerContracts.cs` in the sandbox runner). |
| `DoctorInvocationHandler.cs` | HTTP body  ?  DoctorRequest  ?  brain  ?  DoctorResponse. |
| `DoctorBrain.cs` | LLM call (o4-mini) + SecurityReviewer pass + JSON parse. |
| `Dockerfile` | Multi-stage net10 aspnet image, exposes 8088. |
| `agent.yaml` | Foundry container-agent spec (kind=hosted, protocol=invocations). |
| `agent.manifest.yaml` | Marketplace-style description for the Foundry catalog. |
| `.foundry/agent-metadata.yaml` | ACR target + project endpoint for `foundry deploy`. |

## Build + push (next steps)

The infrastructure was provisioned in `AgenticStationFuelHub` resource
group on 2026-04-25:

- Foundry account: `AgenticStationFoundry` (kind=AIServices, custom-domain `agenticstationfoundry`).
- Project: `default`. Endpoint: `https://agenticstationfoundry.services.ai.azure.com/api/projects/default`.
- ACR: `crashfoundry32860` (Basic, swedencentral). MSI of the Foundry account has AcrPull.
- Model deployment `ash-doctor`: o4-mini 2025-04-16, Standard, cap 50.

To build + push the image to ACR (recommended remote build to avoid the
slow x86-on-arm QEMU emulation we hit on the orchestrator host):

```powershell
$env:AZURE_EXTENSION_DIR = "C:\Users\ssguotti\.azure\cliextensions_clean"
az acr build `
  --registry crashfoundry32860 `
  --image ash-doctor:v1 `
  --file Dockerfile `
  .
```

To create the hosted-agent resource on the Foundry project (CLI 2.72 has no
direct `az foundry` subgroup; use `az rest` against ARM):

```powershell
# TODO once we know the exact ARM body shape from the Foundry docs.
# Track in the migration session memory.
```

## Local dev

```powershell
# Set credentials for a quick smoke test (uses API key path):
$env:AZURE_OPENAI_ENDPOINT = "https://agenticstationfoundry.services.ai.azure.com/"
$env:AZURE_OPENAI_DEPLOYMENT = "ash-doctor"
$env:AZURE_OPENAI_KEY = "<key from az cognitiveservices account keys list>"

dotnet run

# In another terminal:
$body = @{
  command = "remediate"
  workspace = "/tmp"
  failedStepId = 1
  errorTail = "deployment failed: missing AZURE_LOCATION"
  plan = @{
    steps = @(@{ id = 1; description = "azd up"; cmd = "azd up --no-prompt"; cwd = "." })
  }
} | ConvertTo-Json -Depth 6 -Compress

curl -X POST http://localhost:8088/invocations `
  -H "Content-Type: application/json" -d $body
```

[1]: https://learn.microsoft.com/azure/ai-foundry/concepts/hosted-agents
