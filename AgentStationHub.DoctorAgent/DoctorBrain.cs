// extern alias keeps DefaultAzureCredential unambiguous: the
// Azure.AI.AgentServer.Invocations preview pulls in an Azure.Core build
// that re-exports the same type name as Azure.Identity 1.15. Without the
// alias, both surface in the global namespace and CS0433 fires even with
// fully-qualified names.
extern alias AzId;

using System.ClientModel;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace AgentStationHub.DoctorAgent;

/// <summary>
/// Hosted-Doctor v0 brain. Single-shot LLM call against the
/// 'ash-doctor' deployment on AgenticStationFoundry, plus a SecurityReviewer
/// audit pass. No tools � the orchestrator pre-bundles relevant repo files
/// in DoctorRequest.RepoFiles.
///
/// Prompt note: the in-sandbox Doctor (PlanningTeam.cs DoctorInstructions)
/// is ~1100 lines of curated failure-signature knowledge that took many
/// iterations to build. We do NOT duplicate it here yet � the v0 prompt
/// below is a condensed version sufficient to exercise the Foundry pipeline
/// end-to-end. Once we confirm the hosted invocation path works, the next
/// iteration will extract the full prompt to a shared file
/// (Shared/DoctorPrompt.cs) linked into both projects so a single source of
/// truth survives.
/// </summary>
public sealed class DoctorBrain
{
    private readonly ChatClient _chat;
    private readonly ChatCompletionOptions _opts;
    private readonly ILogger<DoctorBrain> _log;

    public DoctorBrain(IConfiguration cfg, ILogger<DoctorBrain> log)
    {
        _log = log;

        // Endpoint discovery. The Foundry hosted-agent runtime auto-injects
        // FOUNDRY_PROJECT_ENDPOINT which points at the project URL
        // (https://X.services.ai.azure.com/api/projects/<name>) � that's
        // the Foundry data-plane root, NOT the Azure OpenAI endpoint.
        // AzureOpenAIClient must talk to https://X.openai.azure.com/ (or
        // .cognitiveservices.azure.com) to resolve /openai/deployments/{d}.
        // We therefore prefer AZURE_OPENAI_ENDPOINT (baked into the image)
        // and fall back to FOUNDRY_PROJECT_ENDPOINT only as a last resort.
        // Reading via Environment.GetEnvironmentVariable instead of
        // IConfiguration to bypass any AgentHost.CreateBuilder() provider
        // inconsistencies.
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? cfg["AZURE_OPENAI_ENDPOINT"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? cfg["FOUNDRY_PROJECT_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "missing AZURE_OPENAI_ENDPOINT / FOUNDRY_PROJECT_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
            ?? cfg["AZURE_OPENAI_DEPLOYMENT"]
            ?? "ash-doctor";

        // The hosted-agent sidecar runs as a managed identity that has
        // 'Cognitive Services User' on the parent Foundry account, so
        // DefaultAzureCredential picks it up transparently. API key is
        // supported for local dev only.
        // o4-mini reasoning models REQUIRE api-version 2024-12-01-preview or later.
        // Azure.AI.OpenAI 2.2.0-beta.4's default ServiceVersion may be older,
        // which causes the upstream call to fail with HTTP 400 "Model o4-mini
        // is enabled only for api versions 2024-12-01-preview and later".
        // The Foundry hosted-agent runtime then surfaces the failure as an
        // opaque HTTP 500 with empty body. Pin the version explicitly.
        var aoaiOpts = new AzureOpenAIClientOptions(
            AzureOpenAIClientOptions.ServiceVersion.V2025_01_01_Preview);

        AzureOpenAIClient azc;
        var apiKey = cfg["AZURE_OPENAI_KEY"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            azc = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey), aoaiOpts);
            _log.LogInformation("doctor: using API-key auth (local dev path)");
        }
        else
        {
            // Fully-qualified to avoid the DefaultAzureCredential type-clash
            // that surfaces when Azure.Core 1.53+ is pulled transitively
            // alongside Azure.Identity (both expose the same type name).
            azc = new AzureOpenAIClient(new Uri(endpoint),
                new AzId::Azure.Identity.DefaultAzureCredential(), aoaiOpts);
            _log.LogInformation("doctor: using DefaultAzureCredential (managed identity)");
        }

        _chat = azc.GetChatClient(deployment);
        // o4-mini reasoning models reject the legacy 'max_tokens'
        // parameter and require 'max_completion_tokens'. Different
        // OpenAI SDK versions disagree on which JSON key
        // ChatCompletionOptions.MaxOutputTokenCount serializes to
        // (Azure.AI.OpenAI 2.9.0-beta.1 + OpenAI 2.9.1 still emit the
        // legacy key, triggering HTTP 400 unsupported_parameter). The
        // default (no cap) is fine: o4-mini's reasoning budget is set
        // server-side and our remediation JSON is < 1KB. Leave the
        // options minimal so we don't accidentally re-introduce the
        // bad parameter.
        _opts = new ChatCompletionOptions();
        _log.LogInformation("doctor: endpoint={Endpoint} deployment={Deployment}",
            endpoint, deployment);
    }

    public async Task<(RemediationDto remediation, List<TraceLine> trace)>
        RemediateAsync(DoctorRequest req, CancellationToken ct)
    {
        var trace = new List<TraceLine>();
        var prompt = BuildPrompt(req);
        trace.Add(new TraceLine
        {
            Agent = "DeploymentDoctor",
            Stage = "input",
            Message = $"prompt {prompt.Length} chars, files {req.RepoFiles?.Count ?? 0}",
        });

        // Pass 1: Doctor proposes a remediation.
        var raw = await CallLlmAsync(DoctorInstructions, prompt, ct);
        trace.Add(new TraceLine
        {
            Agent = "DeploymentDoctor",
            Stage = "output",
            Message = Truncate(raw, 500),
        });

        // Pass 2: SecurityReviewer audits it. Returns same JSON if safe,
        // a sanitized version otherwise.
        var reviewed = await CallLlmAsync(SecurityReviewerInstructions,
            $"Remediation to review (return corrected or same):\n{raw}", ct);
        trace.Add(new TraceLine
        {
            Agent = "SecurityReviewer",
            Stage = "remediation",
            Message = Truncate(reviewed, 400),
        });

        var remediation = ParseRemediation(reviewed, req.FailedStepId ?? 0);
        return (remediation, trace);
    }

    private async Task<string> CallLlmAsync(string system, string user, CancellationToken ct)
    {
        var resp = await _chat.CompleteChatAsync(
            new ChatMessage[]
            {
                new SystemChatMessage(system),
                new UserChatMessage(user),
            },
            _opts,
            ct);
        var text = string.Concat(resp.Value.Content.Select(p => p.Text ?? ""));
        return text.Trim();
    }

    private static string BuildPrompt(DoctorRequest req)
    {
        var sb = new StringBuilder();

        if (req.PriorInsights is { Count: > 0 })
        {
            sb.AppendLine("PRIOR LEARNINGS (treat as hints, verify):");
            foreach (var i in req.PriorInsights
                .Where(x => x.Confidence >= 0.5)
                .OrderByDescending(x => x.Confidence)
                .Take(15))
            {
                sb.AppendLine($"  - [{i.Key}] (conf={i.Confidence:0.0}): {i.Value}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("FAILED STEP:");
        var failed = req.Plan?.Steps?.FirstOrDefault(s => s.Id == req.FailedStepId);
        if (failed is not null)
        {
            sb.AppendLine($"  id: {failed.Id}");
            sb.AppendLine($"  description: {failed.Description}");
            sb.AppendLine($"  cmd: {failed.Cmd}");
            sb.AppendLine($"  cwd: {failed.Cwd}");
        }
        sb.AppendLine();

        sb.AppendLine("ERROR OUTPUT (last ~2KB):");
        var tail = req.ErrorTail ?? "";
        sb.AppendLine(tail.Length > 2000 ? tail[^2000..] : tail);
        sb.AppendLine();

        if (req.Plan?.Steps is { Count: > 0 } steps)
        {
            sb.AppendLine("FULL PLAN:");
            foreach (var s in steps)
                sb.AppendLine($"  [{s.Id}] {s.Description} � `{s.Cmd}` (cwd: {s.Cwd})");
            sb.AppendLine();
        }

        if (req.PreviousAttempts is { Count: > 0 } prev)
        {
            sb.AppendLine("PREVIOUS ATTEMPTS (do NOT repeat):");
            foreach (var p in prev) sb.AppendLine("  - " + p);
            sb.AppendLine();
        }

        if (req.RepoFiles is { Count: > 0 } files)
        {
            sb.AppendLine("REPO FILES (pre-bundled by the orchestrator; truncated to 64 KB each):");
            foreach (var (path, content) in files)
            {
                sb.AppendLine($"=== {path} ===");
                sb.AppendLine(content.Length > 16_000 ? content[..16_000] + "..." : content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Prompt � v0 condensed. See class-level note for the migration plan
    // toward the full prompt at Shared/DoctorPrompt.cs.
    // -----------------------------------------------------------------------
    private const string DoctorInstructions = """
        You are a deployment remediation engineer. A single step in a running
        deployment plan has failed. Analyse the error output, plan, prior
        attempts and any pre-bundled repo files, and propose a CONCRETE
        machine-executable fix.

        THREE OUTCOMES:
          - "replace_step"  - substitute the failing step with a corrected
                              command (same step id, used for the CURRENTLY
                              failing step or any future step).
          - "insert_before" - add prep steps BEFORE the failing step. The
                              failing step retries unchanged.
          - "give_up"       - error genuinely unrecoverable (auth lost,
                              quota gone, requires user input). Use the
                              special "[Escalate] " prefix in 'reasoning'
                              when the failure is in the REPO SOURCE
                              itself (missing Dockerfile, broken Bicep,
                              corrupt lockfile) so the orchestrator can
                              mark the session BlockedNeedsHumanOrSourceFix
                              and surface a repo-source PR proposal to
                              the user instead of more retries.

        REPO HAS NO DEPLOYMENT SCAFFOLDING -> ESCALATE, DO NOT INVENT
          If the failing step is `azd up` / `azd provision` / `azd deploy`
          / `azd env new` and the repo has NO `azure.yaml` (check the
          inspection summary / pre-bundled repo files), OR the failing
          step is `terraform <x>` and the repo has NO `*.tf`, OR the
          failing step is `az deployment group create -f <bicep>` and the
          referenced Bicep file does not exist:
            -> EMIT `give_up` with reasoning prefixed `[Escalate] `
               explaining that the repository does not contain the
               required deployment artifacts (e.g. "no azure.yaml at
               repo root; this repo is documented as local-dev only").
          DO NOT invent commands such as `azd init --template-empty`,
          `azd init --from-code`, scaffolding a fresh `azure.yaml`,
          generating a Bicep file from scratch, or any other action
          that synthesizes deployment files the user did not author.
          The deploy must fail explicitly so the operator knows the
          source repo is not deployable as-is.

        BAKED HELPERS (sandbox image v32 — PREFER THESE OVER RAW SHELL)
          The sandbox ships single-token, no-quote scripts in /usr/local/bin.
          When a helper covers your remediation, EMIT THE HELPER:
            relocate-node-modules <root>            relocate-venv <root>
            agentic-azd-env-prime [<env-file>]      agentic-azd-up [args]
            agentic-azd-deploy [args]               agentic-acr-build <ctx> <df> <img>
            agentic-build <ctx> <df> <img> [tmo]    agentic-npm-install <dir>
            agentic-dotnet-restore <target>         agentic-bicep <file>
            agentic-clone <url> <dst>               agentic-aca-wait <app> <rg> [tmo]
            agentic-summary
          NEVER emit multi-line bash with nested quotes when a helper exists.
          NEVER wrap a helper in `bash -lc "..."`. The Verifier rejects nested
          quoting and your remediation will be discarded.

        HARD RULES
          - Commands MUST start with one of: git, azd, az, pac, docker,
            dotnet, npm, node, python, pip, bash, sh, make, terraform,
            sed, find, chmod, dos2unix, tr.
            ('pwsh' is NOT installed - use 'bash -lc "..."' for substitutions.)
          - Forbidden: 'rm -rf /', 'curl|sh', remote shell exec, hard-coded
            credentials, writes outside /workspace.
          - Use non-interactive flags (--no-prompt, --yes, -y).
          - Do NOT repeat a remediation listed under PREVIOUS ATTEMPTS.
          - If 3+ previous attempts share the same error signature, prefer
            'give_up' with a diagnostic over a new speculative attempt.
          - Keep newSteps concise (1-3 steps).

        DO NOT DEGRADE THE DEPLOY MODEL:
          - DO NOT replace 'azd up' with 'azd provision' alone.
          - DO NOT set AZURE_SERVICE_<NAME>_RESOURCE_EXISTS=true to skip
            build failures (leaves Container Apps on placeholder image).
          - DO NOT remove 'docker:' from azure.yaml to bypass runtime check.

        TYPED ACTIONS - PREFER THESE OVER inline shell pipelines:
          a) ACR remote build:
             "action": { "type":"AcrBuild", "service":"<svc>",
                         "contextDir":"<rel>", "dockerfile":"Dockerfile",
                         "imageName":"<svc>" }
          b) Container App promote-to-image:
             "action": { "type":"ContainerAppUpdate", "service":"<svc>",
                         "imageRef":"$LASTBUILT" }
          c) azd env set without shell substitution:
             "action": { "type":"AzdEnvSet", "key":"AZURE_SUBSCRIPTION_ID",
                         "valueFrom":"AzAccountSubscriptionId" }
          d) Pure shell tweak:
             "action": { "type":"Bash",
                         "script":"sed -i 's/old/new/g' infra/main.bicep" }

        OUTPUT - respond ONLY with a JSON object:
        {
          "kind": "replace_step" | "insert_before" | "give_up",
          "stepId": <int>,
          "newSteps": [
            { "id": <int>, "description": "...", "cmd": "...", "cwd": "." }
            // OR replace "cmd" with an "action" object (see TYPED ACTIONS)
          ],
          "reasoning": "one or two sentences, no markdown"
        }
        Every id and stepId MUST be a whole integer.
        """;

    private const string SecurityReviewerInstructions = """
        Audit a deployment remediation for SAFETY and COHERENCE.
        - Forbidden: 'rm -rf /', 'curl|sh', remote shell exec, hard-coded
          credentials, writes outside /workspace.
        - Commands must start with one of: git, azd, az, pac, docker,
          dotnet, npm, node, python, pip, bash, sh, make, terraform,
          sed, find, chmod, dos2unix, tr. ('pwsh' is NOT installed.)
        - Interactive flags must be replaced with non-interactive equivalents.
        Return the remediation JSON UNCHANGED if safe, otherwise return a
        corrected version. Respond with ONLY the JSON object.
        """;

    private static RemediationDto ParseRemediation(string json, int failedStepId)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        var clean = (start >= 0 && end > start) ? json[start..(end + 1)] : "{}";

        try
        {
            using var doc = JsonDocument.Parse(clean);
            var r = doc.RootElement;

            var kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "give_up" : "give_up";
            var stepId = r.TryGetProperty("stepId", out var sid)
                ? ReadIntTolerant(sid, failedStepId) : failedStepId;
            var reasoning = r.TryGetProperty("reasoning", out var rs) ? rs.GetString() : null;

            var newSteps = new List<StepDto>();
            if (r.TryGetProperty("newSteps", out var ns) && ns.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ns.EnumerateArray())
                {
                    string? actionJson = null;
                    if (s.TryGetProperty("action", out var actEl)
                        && actEl.ValueKind == JsonValueKind.Object)
                    {
                        actionJson = actEl.GetRawText();
                    }
                    newSteps.Add(new StepDto
                    {
                        Id = s.TryGetProperty("id", out var sIdEl)
                            ? ReadIntTolerant(sIdEl, stepId) : stepId,
                        Description = s.TryGetProperty("description", out var d)
                            ? d.GetString() ?? "" : "",
                        Cmd = s.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "",
                        Cwd = s.TryGetProperty("cwd", out var w) ? w.GetString() ?? "." : ".",
                        ActionJson = actionJson,
                    });
                }
            }

            return new RemediationDto
            {
                Kind = kind,
                StepId = stepId,
                NewSteps = newSteps,
                Reasoning = reasoning,
            };
        }
        catch (Exception ex)
        {
            return new RemediationDto
            {
                Kind = "give_up",
                StepId = failedStepId,
                Reasoning = $"hosted Doctor failed to parse LLM JSON: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// LLMs occasionally return decimals like 9.5 ('insert BETWEEN 9 and
    /// 10') or strings. Coerce to nearest int, fall back when impossible.
    /// </summary>
    private static int ReadIntTolerant(JsonElement el, int fallback) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetInt32(out var i) => i,
        JsonValueKind.Number when el.TryGetDouble(out var d) => (int)Math.Round(d),
        JsonValueKind.String when int.TryParse(el.GetString(), out var si) => si,
        _ => fallback,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
