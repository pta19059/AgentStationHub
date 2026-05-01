using System.Text.Json;
using AgentStationHub.Models;
using AgentStationHub.Services.Security;
using AgentStationHub.Services.Tools;
using OpenAI.Chat;

namespace AgentStationHub.Services.Agents;

/// <summary>
/// "Meta-Doctor". Last line of defence before a session is marked
/// <c>BlockedNeedsHumanOrSourceFix</c>: when the in-sandbox / hosted
/// Doctor returns an <c>[Escalate]</c> verdict and our deterministic
/// <c>TryAutoPatchEscalation</c> regex table does NOT recognise the
/// signature, this agent gets the failing command + log tail + Doctor
/// reasoning + previous attempts and is asked to propose a single
/// rerunnable shell command (or a small sequence) that would fix the
/// issue.
///
/// Output is a strict JSON object validated against
/// <see cref="ResolverDecision"/>; bad / unparseable output ⇒ null
/// (caller falls back to the existing escalate path). Every emitted
/// step is run through <see cref="PlanValidator"/> before it lands in
/// the live plan, so the resolver cannot smuggle in destructive shell.
///
/// Why an LLM instead of more regex: the failure space (ARM error
/// codes, Bicep validation, azd hooks, ACR build, ACA secrets) is
/// long-tail. Hand-coding every signature into the orchestrator is
/// what keeps the operator copy-pasting errors. This agent absorbs
/// the long tail; the hard-coded patterns stay as a free fast-path
/// for the cases we already know.
/// </summary>
public sealed class EscalationResolverAgent
{
    private readonly ChatClient _chat;
    private readonly ILogger<EscalationResolverAgent> _log;
    private readonly AzureModelCatalogProbe? _modelProbe;

    public EscalationResolverAgent(
        ChatClient chat,
        ILogger<EscalationResolverAgent> log,
        AzureModelCatalogProbe? modelProbe = null)
    {
        _chat = chat;
        _log = log;
        _modelProbe = modelProbe;
    }

    private const string SystemPrompt = """
        You are the EscalationResolver. The deployment Doctor has just
        given up on a single failing step inside a Linux sandbox
        container that is already logged in to Azure (az, azd) and has
        docker, dotnet, node, python3, jq, terraform. The give-up may
        be either an "[Escalate]" verdict (Doctor thinks the repo is
        the bug) OR a regular give-up (Doctor sees the failure but
        cannot synthesise a fix). In BOTH cases your job is identical.

        Your only job: propose ONE shell command (or a very short
        sequence) that, when executed in the same sandbox, has a real
        chance to make that step succeed on retry. The Doctor's
        "doctorReasoning" field often contains a hint (e.g. "switch to
        a remote build approach", "upgrade the docker client",
        "increase quota") — turn that hint into an executable command
        when it is actionable inside the sandbox.

        STRICT RULES
        1. Output JSON ONLY, matching this schema:
           {
             "kind": "replace_step" | "insert_before" | "give_up",
             "command": "<single-line shell command>",
             "rationale": "<one short sentence>",
             "confidence": 0.0-1.0
           }
        2. The command MUST start with one of:
             az  azd  docker  dotnet  npm  node  python  python3  pip
             pwsh  bash  sh  git  make  terraform  jq
             agentic-azd-up  agentic-azd-env-prime  agentic-azd-deploy
             agentic-acr-build  agentic-build  agentic-npm-install
             agentic-dotnet-restore  agentic-bicep  agentic-clone
             agentic-aca-wait  agentic-help  agentic-summary
             relocate-node-modules  relocate-venv
        3. NO `rm -rf /`, NO `curl ... | sh`, NO `Invoke-Expression`.
           NO interactive prompts (always pass --yes / --no-prompt /
           --use-device-code style flags).
        4. NO commands that re-clone or re-bootstrap the repository
           (the workspace is already cloned at the working dir).
        5. Prefer the SMALLEST surgical fix. Examples:
           • ARM "InvalidPrincipalId" on a role-assignment sub-deployment
             → wrap the original `az deployment` call in `bash -lc 'PID=$(az ad signed-in-user show --query id -o tsv); … principalId=$PID'`.
           • ARM "ContainerAppSecretInvalid" because empty secret params
             → re-run the same `az deployment` after stripping `name=''`.
           • Bicep references a deprecated/unsupported AOAI model
             → `bash -lc "grep -rl 'old-model' /workspace --include='*.bicep' | xargs sed -i 's/old-model/new-model/g'"`.
           • azd hook fails because env_file missing → `touch <missing>`.
           • Container App not found → `agentic-aca-wait <service>`.
           • Strategist used `az deployment sub create` but `azure.yaml`
             exists in a subdir → propose `bash -lc "cd <subdir> && azd up --no-prompt"`
             and use kind="replace_step".
        6. "kind":
            - "replace_step": replace the failing step with this command
              (use this when the failing step is itself the wrong
               approach, e.g. wrong cwd, missing param, deprecated model).
            - "insert_before": run this command BEFORE the failing step
              and then re-execute the original step (use this when the
              failing step is correct but the environment needs a fix,
              e.g. sed across files, az role assignment).
            - "give_up": only when the error is genuinely a source-repo
              bug (broken Dockerfile, missing required code) AND no
              sandbox-side patch is plausible.
        7. Look at "previousAttempts" — DO NOT propose anything that
           was already tried (you would just re-fail the same way).
        8. AUTHORITATIVE GROUNDING: when the input contains a non-empty
           "azureModelCatalog" block, treat it as the ground truth for
           which (model name, version) pairs are deployable in the
           current region. If you propose a Bicep/azd model swap, BOTH
           the new name AND the new version MUST appear together in
           that catalog. NEVER propose a (name, version) that is not
           listed there — it WILL fail ARM validation and burn another
           remediation attempt. If no replacement in the catalog is a
           reasonable substitute for the deprecated/unsupported model,
           return "give_up" instead of guessing.

        Output JSON ONLY. No markdown, no prose, no code fences.
        """;

    public async Task<Remediation?> ResolveAsync(
        DeploymentStep failingStep,
        string failingCommand,
        string stepTail,
        string doctorReasoning,
        IReadOnlyList<string> previousAttempts,
        CancellationToken ct,
        string? azureRegion = null)
    {
        // Live-fetch the AOAI model catalog for the target region so the
        // resolver proposes (model, version) pairs that actually exist —
        // instead of hallucinating retired versions and ping-ponging
        // with the Doctor. Cached 24 h in-process. Empty string when the
        // probe is unavailable; the resolver still works without it.
        string modelCatalogBlock = "";
        if (_modelProbe is not null && !string.IsNullOrWhiteSpace(azureRegion))
        {
            try
            {
                modelCatalogBlock = await _modelProbe
                    .GetCatalogPromptBlockAsync(azureRegion, ct);
            }
            catch (Exception ex)
            {
                _log.LogInformation(ex,
                    "AzureModelCatalogProbe failed; resolver will run without live catalog grounding.");
            }
        }

        var userPayload = new
        {
            failingStep = new
            {
                id = failingStep.Id,
                description = failingStep.Description,
                command = failingCommand,
                workingDirectory = failingStep.WorkingDirectory,
            },
            azureRegion = azureRegion ?? "",
            azureModelCatalog = modelCatalogBlock,
            errorTail = Truncate(stepTail, 6000),
            doctorReasoning = Truncate(doctorReasoning, 2000),
            previousAttempts = previousAttempts.Take(20).ToArray(),
        };

        var userJson = JsonSerializer.Serialize(userPayload);

        try
        {
            var opts = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            };
            var resp = await _chat.CompleteChatAsync(
                new ChatMessage[]
                {
                    new SystemChatMessage(SystemPrompt),
                    new UserChatMessage(userJson),
                },
                opts,
                ct);

            var content = resp.Value.Content.FirstOrDefault()?.Text ?? "";
            if (string.IsNullOrWhiteSpace(content))
            {
                _log.LogInformation("EscalationResolver returned empty content.");
                return null;
            }

            var decision = ParseDecision(content);
            if (decision is null)
            {
                _log.LogInformation(
                    "EscalationResolver output was not parseable JSON: {Raw}",
                    Truncate(content, 500));
                return null;
            }

            if (string.Equals(decision.Kind, "give_up", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation(
                    "EscalationResolver chose give_up: {Rationale}", decision.Rationale);
                return null;
            }

            var cmd = decision.Command?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(cmd))
            {
                _log.LogInformation("EscalationResolver returned empty command.");
                return null;
            }

            // Reject if the resolver echoed the failing command verbatim
            // (would re-fail identically). Heuristic: trim + lowercase.
            if (string.Equals(
                Normalise(cmd), Normalise(failingCommand), StringComparison.Ordinal))
            {
                _log.LogInformation("EscalationResolver echoed the failing command verbatim — discarding.");
                return null;
            }

            // Same heuristic against previousAttempts so we never loop
            // on an already-tried fix.
            foreach (var prev in previousAttempts)
            {
                if (!string.IsNullOrWhiteSpace(prev)
                    && prev.Contains(Truncate(cmd, 80), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("EscalationResolver suggestion was already attempted — discarding.");
                    return null;
                }
            }

            var kind = string.Equals(decision.Kind, "replace_step", StringComparison.OrdinalIgnoreCase)
                ? "replace_step"
                : "insert_before";

            var step = new DeploymentStep(
                Id: 0,
                Description:
                    $"[EscalationResolver] {decision.Rationale ?? "auto-resolved escalation"} " +
                    $"(confidence={decision.Confidence:0.00})",
                Command: cmd,
                WorkingDirectory: failingStep.WorkingDirectory ?? ".");

            // Run the synthesised step through the same security validator
            // that gates every Strategist plan. If the resolver picked a
            // disallowed binary or smuggled in a `curl | sh`, drop the
            // suggestion silently and let the caller surface the original
            // escalate verdict.
            var (ok, reason) = PlanValidator.Validate(step);
            if (!ok)
            {
                _log.LogWarning(
                    "EscalationResolver suggestion rejected by PlanValidator: {Reason}. Cmd: {Cmd}",
                    reason, Truncate(cmd, 200));
                return null;
            }

            return new Remediation(
                Kind: kind,
                StepId: failingStep.Id,
                NewSteps: new[] { step },
                Reasoning:
                    $"[escalation-resolver] {decision.Rationale} " +
                    $"(kind={kind}, confidence={decision.Confidence:0.00})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EscalationResolver call failed; falling back to give-up path.");
            return null;
        }
    }

    private static ResolverDecision? ParseDecision(string raw)
    {
        // The model is instructed to emit pure JSON, but be lenient and
        // strip ```json fences if it slips up.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }
        try
        {
            return JsonSerializer.Deserialize<ResolverDecision>(
                trimmed,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[^max..]);

    private static string Normalise(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private sealed class ResolverDecision
    {
        public string? Kind { get; set; }
        public string? Command { get; set; }
        public string? Rationale { get; set; }
        public double Confidence { get; set; }
    }
}
