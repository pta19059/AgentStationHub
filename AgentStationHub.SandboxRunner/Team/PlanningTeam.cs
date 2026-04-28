using System.Text;
using System.Text.Json;
using AgentStationHub.SandboxRunner.Contracts;
using AgentStationHub.SandboxRunner.Inspection;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace AgentStationHub.SandboxRunner.Team;

/// <summary>
/// Sequential multi-agent orchestration for deployment planning.
///
///   Scout (deterministic) ? TechClassifier (LLM) ? Strategist (LLM) ? Reviewer (LLM)
///
/// Each LLM agent is a thin wrapper around Microsoft.Agents.AI's AIAgent
/// created from an OpenAI ChatClient via the AsAIAgent extension.
///
/// NOTE on the Responses API: Microsoft.Agents.AI.OpenAI 1.1.0 targets
/// OpenAI 2.9.x where the Responses client was renamed to 'ResponsesClient',
/// but Azure.AI.OpenAI has not yet surfaced a matching 'GetResponsesClient'
/// method on AzureOpenAIClient. Until the two stacks line up we stay on
/// ChatClient here and keep Responses API in the main app (VerifierAgent).
/// The deployment used for these agents must be chat-capable (e.g. gpt-5.3-chat).
/// </summary>
public sealed class PlanningTeam
{
    private readonly ChatClient _chatClient;
    private readonly Action<AgentTraceDto> _trace;

    public PlanningTeam(ChatClient chatClient, Action<AgentTraceDto> trace)
    {
        _chatClient = chatClient;
        _trace = trace;
    }

    public async Task<DeploymentPlanDto> RunAsync(
        string repoUrl, string workspace, string? azureLocation, CancellationToken ct,
        IReadOnlyList<PriorInsightDto>? priorInsights = null)
    {
        var region = string.IsNullOrWhiteSpace(azureLocation) ? "eastus" : azureLocation.Trim().ToLowerInvariant();

        // Surface the count so the orchestrator's log shows whether the
        // memory store actually fed the planning pipeline.
        if (priorInsights is { Count: > 0 })
            _trace(new AgentTraceDto("Memory", "loaded",
                $"Received {priorInsights.Count} prior insight(s) from previous deploys."));

        // ---- Scout (deterministic, runs inside the sandbox on /workspace) ----
        _trace(new AgentTraceDto("Scout", "start", $"Scanning workspace {workspace}"));
        var manifest = RepoInspector.Inspect(workspace);
        _trace(new AgentTraceDto("Scout", "done",
            "Detected: " + (string.Join(", ", manifest.Summary()) is { Length: > 0 } s ? s : "none")));
        foreach (var r in manifest.Rationale)
            _trace(new AgentTraceDto("Scout", "rationale", r));

        // ---- TechClassifier (LLM): narrow + enrich the tech choice ----
        var classifier = _chatClient.AsAIAgent(
            name: "TechClassifier",
            instructions: """
                You classify a GitHub repository from its toolchain manifest
                and key files. Produce a JSON object:
                {
                  "primaryLanguage": "python|typescript|csharp|java|go|rust|other",
                  "deploymentFamily": "azd|docker|terraform|bicep|npm|make|custom|none",
                  "repoKind": "app|course|library|samples|docs|cli|monorepo|unknown",
                  "deployable": true|false,
                  "notDeployableReason": "<user-facing sentence, only when deployable=false>",
                  "reasoning": "one sentence why",
                  "requires": ["node20", "python3.11", "bicep", ...]
                }

                REPO KIND � use these rules:
                � "app": a deployable application. Must have at least ONE of:
                  azure.yaml, docker-compose.yml, Dockerfile+IaC (bicep/tf),
                  or 'deploy' script in package.json.
                � "course": curriculum / lessons / tutorial. Signals:
                  notebooks > 5, lesson folders > 0, README title contains
                  'course'/'tutorial'/'lessons'/'beginners'/'learn',
                  no deployment entrypoint.
                � "library": a reusable package published to a registry.
                  pyproject.toml without azure.yaml, package.json without
                  'deploy' script, no infra.
                � "samples": mixed demo snippets without a single deploy
                  target (often monorepo with many sub-READMEs).
                � "docs": static documentation site (mkdocs.yml, _config.yml
                  Jekyll, docusaurus.config.js) without other app code.
                � "cli": command-line tool, install-only (no deploy concept).
                � "monorepo": multiple independent deployable sub-projects.

                DEPLOYABLE:
                � deployable=true ONLY when the repo as a whole has a clear
                  deployment target: kind is "app", OR "monorepo" with a
                  unambiguous primary service.
                � deployable=false for "course", "library" (unless the
                  library's repo also ships a demo app), "samples", "docs",
                  "cli", and "unknown" where no deploy signals were found.

                IMPORTANT � AZURE.YAML TRUMPS EVERYTHING:
                If the repo has an 'azure.yaml' at the root (or a
                'docker-compose.yml' + a Bicep/Terraform directory),
                the AUTHORS HAVE EXPLICITLY DECLARED a deployment
                target. Regardless of how many sub-projects you see,
                deployable MUST be 'true' and kind is "app" (for a
                single-service azure.yaml) or "monorepo" (for azure.yaml
                with multiple 'services:' entries). Do NOT return false
                just because the repo looks sprawling � azure.yaml is
                the single-point-of-truth that azd will use to pick
                the deploy flow.

                notDeployableReason (REQUIRED when deployable=false):
                Write ONE concise, professional sentence in neutral tone
                that states the nature of the repository and � implicitly �
                why it has no deployment target. Avoid imperative tutorial
                language ("clone it and open VS Code"): the UI already
                appends a generic "Recommended next steps" block, your
                sentence should only describe WHAT the repository is.
                Good examples:
                � "This repository is a learning resource composed of
                   Jupyter notebooks and lesson folders; it does not
                   include an application or infrastructure-as-code to
                   provision."
                � "This repository is a Python package intended to be
                   consumed as a library (installable via pip) rather
                   than deployed as a service."
                � "This repository is a documentation site built with
                   MkDocs and has no deployable application code."
                Keep it under 240 characters, no markdown, no emoji.

                Respond with ONLY the JSON object.
                """);
        var classifierInput = BuildClassifierInput(manifest);
        var classification = await InvokeAsync(classifier, classifierInput, ct);
        _trace(new AgentTraceDto("TechClassifier", "output", Truncate(classification, 500)));

        // ---- Classification gate: short-circuit non-deployable repos ----
        // Without this the Strategist would happily invent a nonsense plan
        // for a course / library / docs repo and the user would waste time
        // watching it fail in execution. Surface the verdict now.
        //
        // BUT: the LLM classifier is non-deterministic on ambiguous repos
        // (monorepos with multiple services + an azure.yaml at root, or
        // samples repos with a legit Dockerfile+Bicep pair). On those we
        // used to flip-flop between 'deployable' and 'not deployable'
        // across retries � exactly the symptom a user reported for
        // Azure-Samples/azure-ai-travel-agents: 1st attempt no-deploy,
        // 3rd attempt starts the deploy.
        //
        // Fix: HARD OVERRIDE the verdict when the repo has a DETERMINISTIC
        // deployment-entry signal (azure.yaml, docker-compose.yml,
        // Dockerfile+IaC, or npm 'deploy' script). These files are an
        // explicit author statement "this is meant to be deployed" and
        // no LLM second-guessing can make them go away. We still capture
        // the LLM's RepoKind for the UI/memory ("monorepo", "app", ...)
        // and nudge the Strategist via the rationale, but we do NOT
        // block execution. Worst case the Strategist can't make progress
        // on a genuinely mis-classified repo, which a user can cancel �
        // far better than the current silent non-deterministic gate.
        var verdict = ParseClassificationVerdict(classification);
        if (verdict is { Deployable: false } && manifest.HasDeploymentEntry)
        {
            _trace(new AgentTraceDto("TechClassifier", "override",
                $"LLM said deployable=false (kind='{verdict.RepoKind}') but the repo has a " +
                "deterministic deployment entry (azure.yaml / docker-compose / Dockerfile+IaC / " +
                "npm deploy). Overriding to deployable=true and continuing with the Strategist."));
            verdict = verdict with { Deployable = true };
        }

        if (verdict is { Deployable: false })
        {
            _trace(new AgentTraceDto("TechClassifier", "gate",
                $"Repo classified as '{verdict.RepoKind}' and NOT deployable. Skipping Strategist."));
            return new DeploymentPlanDto(
                Prerequisites: new List<string>(),
                Environment: new Dictionary<string, string>(),
                Steps: new List<DeploymentStepDto>(),
                VerifyHints: new List<string> { "not_deployable:" + (verdict.RepoKind ?? "unknown") })
            {
                RepoKind = verdict.RepoKind,
                IsDeployable = false,
                NotDeployableReason = verdict.Reason
                    ?? "This repository does not appear to contain a deployable application."
            };
        }

        // ---- Strategist (LLM): produce the executable plan ----
        var strategist = _chatClient.AsAIAgent(
            name: "DeploymentStrategist",
            instructions: StrategistInstructions);
        var strategistInput = BuildStrategistInput(repoUrl, manifest, classification, region, priorInsights);
        var planJson = await InvokeAsync(strategist, strategistInput, ct);
        _trace(new AgentTraceDto("DeploymentStrategist", "output", Truncate(planJson, 400)));

        // ---- Server-side guard: NO 'azure.yaml' -> NEVER 'azd up/provision/
        // deploy/env new' ----
        // The Strategist has been told this in its instructions (Strategy 1b)
        // but LLM compliance with negative constraints is unreliable; a
        // single hallucinated 'azd env new' sends the deploy down a dead
        // end (we burn a Doctor attempt, the Doctor escalates, the user
        // sees BlockedNeedsHumanOrSourceFix even though the repo IS
        // deployable via the Bicep-direct route the Strategist was
        // supposed to pick). Detect the violation deterministically and
        // re-prompt the Strategist with an explicit, in-message
        // directive. Only ONE retry: if the Strategist insists, the
        // Reviewer + runtime will catch the failure.
        if (!manifest.Azd && ContainsForbiddenAzdAgainstNoAzureYaml(planJson, out var forbiddenCmd))
        {
            _trace(new AgentTraceDto("DeploymentStrategist", "warning",
                $"Plan violated Strategy 1b: emitted '{forbiddenCmd}' but the " +
                "repo has no azure.yaml. Re-prompting with explicit Bicep-direct directive."));

            var retryInput = strategistInput +
                "\n\n" +
                "----- VALIDATION FAILURE - YOU MUST CORRECT -----\n" +
                $"Your previous plan emitted '{forbiddenCmd}'.\n" +
                "This repository has NO 'azure.yaml' at the root. The 'azd <up/provision/deploy/env new>' " +
                "family of commands REQUIRES azure.yaml and will fail with 'no project exists'.\n" +
                "\n" +
                "MANDATORY: Re-emit the plan using Strategy 1b (Bicep-direct), reproducing ONLY the " +
                "deployment artifacts the authors actually shipped:\n" +
                "  - If 'infra/*.bicep' exists: az group create / az deployment group create -f <main.bicep>\n" +
                "  - For every Dockerfile that ships an image: agentic-acr-build <ctx> <Dockerfile> <imageName>\n" +
                "  - Wire the resulting images into the deployed Container Apps / App Services via\n" +
                "    'az containerapp update --image' or 'az webapp config container set'.\n" +
                "  - If a deploy.sh / Makefile target exists, prefer reproducing that.\n" +
                "  - DO NOT scaffold a fresh azure.yaml. DO NOT use 'azd init --from-code' or '--template-empty'.\n" +
                "Respond with ONLY the corrected JSON plan.";

            var retryJson = await InvokeAsync(strategist, retryInput, ct);
            _trace(new AgentTraceDto("DeploymentStrategist", "output",
                "(retry after Strategy 1b violation) " + Truncate(retryJson, 360)));
            planJson = retryJson;
        }

        // ---- Reviewer (LLM): final audit. Passes through or amends. ----
        var reviewer = _chatClient.AsAIAgent(
            name: "SecurityReviewer",
            instructions: """
                You review a deployment plan for SAFETY and COHERENCE.
                - Forbidden: 'rm -rf /', 'curl|sh', remote shell exec,
                  hard-coded credentials, writes outside the repo.
                - Every command must start with one of: git, azd, az, pac,
                  docker, dotnet, npm, node, python, pip, bash, sh,
                  make, terraform.
                  ('pwsh' is NOT installed in the sandbox � flag any step
                  using it and replace with 'bash -lc "..."'.)
                - All steps must use non-interactive flags.
                Return the plan UNCHANGED if it is safe, otherwise return a
                corrected version. Respond with ONLY the JSON plan.
                """);
        var reviewedJson = await InvokeAsync(reviewer,
            $"Plan to review (return corrected or same):\n{planJson}", ct);
        _trace(new AgentTraceDto("SecurityReviewer", "output", Truncate(reviewedJson, 400)));

        return ParsePlan(reviewedJson, manifest);
    }

    /// <summary>
    /// Detects whether <paramref name="planJson"/> contains an 'azd' step
    /// of the up/provision/deploy/env-new family. Used as a deterministic
    /// guard against the Strategist ignoring its own Strategy 1b rule
    /// when the repo has no azure.yaml. Substring match on the step
    /// command field is sufficient: the planner only emits a single line
    /// per step with the literal command and we explicitly do NOT want
    /// to false-trigger on, e.g., 'azd env get-values' or 'azd version'.
    /// </summary>
    private static bool ContainsForbiddenAzdAgainstNoAzureYaml(string planJson, out string forbiddenCmd)
    {
        forbiddenCmd = "";
        // Cheap regex against the raw JSON. Whitelisted azd subcommands
        // (env get-values, env list, version) are NOT flagged. The
        // forbidden subcommands are explicitly the ones that require an
        // existing azure.yaml ("project context").
        var rx = new System.Text.RegularExpressions.Regex(
            @"\bazd\s+(up|provision|deploy|env\s+new)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var m = rx.Match(planJson);
        if (!m.Success) return false;
        forbiddenCmd = m.Value;
        return true;
    }

    /// <summary>
    /// Invoked by the orchestrator whenever a plan step fails. Rescans the
    /// workspace (the Scout already has direct /workspace access inside the
    /// container), feeds the failure context to a DeploymentDoctor agent,
    /// and returns a structured remediation: either a replacement step, one
    /// or more preparatory steps to insert before the failed one, or a
    /// "give_up" signal when the error is genuinely unrecoverable (auth,
    /// quota, resource name conflict at the Azure side).
    /// </summary>
    public async Task<RemediationDto> RemediateAsync(
        string workspace,
        DeploymentPlanDto plan,
        int failedStepId,
        string errorTail,
        IReadOnlyList<string> previousAttempts,
        CancellationToken ct,
        IReadOnlyList<PriorInsightDto>? priorInsights = null)
    {
        _trace(new AgentTraceDto("Scout", "start", $"Rescanning {workspace} for remediation"));
        var manifest = RepoInspector.Inspect(workspace);

        if (priorInsights is { Count: > 0 })
            _trace(new AgentTraceDto("Memory", "loaded",
                $"Doctor receiving {priorInsights.Count} prior insight(s)."));

        // When the error hints at a model-catalog mismatch ('deprecated',
        // 'not supported', 'not found in the AI model catalog'), probe Azure
        // live via 'az cognitiveservices model list' and include the real
        // catalog in the Doctor's prompt. Without this the Doctor ends up
        // guessing model names from memory, burning remediation attempts on
        // renames that do not exist in the tenant.
        var catalogSnapshot = await ProbeAzureModelCatalogAsync(errorTail, plan, ct);
        if (!string.IsNullOrEmpty(catalogSnapshot))
        {
            // Count how many regions made it into the snapshot for the trace.
            var regionCount = System.Text.RegularExpressions.Regex
                .Matches(catalogSnapshot, @"^---\s*Region:", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Count;
            _trace(new AgentTraceDto("AzureCatalogProbe", "hit",
                $"Retrieved live OpenAI model catalog from {Math.Max(regionCount, 1)} region(s) " +
                $"({catalogSnapshot.Length} chars) for the Doctor."));
        }

        // Build the Doctor agent. Unlike the other single-shot agents in
        // this pipeline, the Doctor is wired with INSPECTION TOOLS so it
        // can read the actual repo files + probe the live sandbox state
        // before proposing a fix. This removes the classic "speculative
        // sed chain" failure mode where the Doctor patches a file whose
        // contents it had imagined rather than read. The Agent Framework
        // drives a multi-turn tool loop automatically: the Doctor calls
        // read_workspace_file / list_workspace_directory / run_diagnostic
        // / check_tool_available until it has enough context, then emits
        // the final JSON remediation.
        var toolbox = new DoctorToolbox(workspace,
            (lvl, line) => _trace(new AgentTraceDto("DeploymentDoctor", lvl, line)));
        var doctor = _chatClient.AsAIAgent(
            name: "DeploymentDoctor",
            instructions: DoctorInstructions,
            tools: toolbox.AsAITools().ToList());

        var input = BuildDoctorInput(manifest, plan, failedStepId, errorTail, previousAttempts, catalogSnapshot, priorInsights);

        // Start a heartbeat task: LLM inference on a large prompt can take
        // 20-60s and during that window the Live log is silent. Without
        // feedback the user cannot tell if the Doctor is thinking or
        // stuck. Emit a trace line every 10s until the call returns.
        _trace(new AgentTraceDto("DeploymentDoctor", "thinking",
            $"Analysing failure (prompt size: {input.Length} chars, previous attempts: {previousAttempts.Count})..."));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = Task.Run(async () =>
        {
            var elapsed = 10;
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), heartbeatCts.Token); }
                catch (OperationCanceledException) { return; }
                _trace(new AgentTraceDto("DeploymentDoctor", "thinking",
                    $"Still analysing (~{elapsed}s)..."));
                elapsed += 10;
            }
        });

        string raw;
        try
        {
            raw = await InvokeAsync(doctor, input, ct);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* expected on cancel */ }
        }
        _trace(new AgentTraceDto("DeploymentDoctor", "output", Truncate(raw, 500)));

        // Audit pass: run the security reviewer against the remediation to
        // make sure the doctor did not introduce a dangerous command.
        var reviewer = _chatClient.AsAIAgent(
            name: "SecurityReviewer",
            instructions: """
                Audit a deployment remediation for SAFETY and COHERENCE.
                - Forbidden: 'rm -rf /', 'curl|sh', remote shell exec,
                  hard-coded credentials, writes outside /workspace.
                Commands must start with one of: git, azd, az, pac, docker,
                dotnet, npm, node, python, pip, bash, sh, make, terraform,
                sed, find, chmod, dos2unix, tr.
                ('pwsh' is NOT installed � reject any remediation using it.)
                - Interactive flags must be replaced with non-interactive
                  equivalents.
                Return the remediation JSON UNCHANGED if safe, otherwise
                return a corrected version. Respond with ONLY the JSON object.
                """);
        var reviewed = await InvokeAsync(reviewer,
            $"Remediation to review (return corrected or same):\n{raw}", ct);
        _trace(new AgentTraceDto("SecurityReviewer", "remediation", Truncate(reviewed, 400)));

        return ParseRemediation(reviewed, failedStepId);
    }

    private const string DoctorInstructions = """
        You are a deployment remediation engineer. A single step in a running
        deployment plan has failed. Your job: analyse the error output, the
        plan context, and the repository manifest, and propose a CONCRETE
        machine-executable fix.

        BAKED HELPERS (sandbox image v32 — PREFER THESE OVER RAW SHELL):
          relocate-node-modules <root>           relocate-venv <root>
          agentic-azd-env-prime [<env-file>]     agentic-azd-up [args]
          agentic-azd-deploy [args]              agentic-acr-build <ctx> <df> <img>
          agentic-build <ctx> <df> <img> [tmo]   agentic-npm-install <dir>
          agentic-dotnet-restore <target>        agentic-bicep <file>
          agentic-clone <url> <dst>              agentic-aca-wait <app> <rg> [tmo]
          agentic-summary
        Single-token, no nested quotes, idempotent. NEVER wrap a helper
        invocation in `bash -lc "..."`. When a helper covers your fix,
        emit the helper. Use raw shell only for sed/find one-liners that
        no helper covers.

        ESCALATE TO REPO SOURCE WHEN APPROPRIATE
          When the failure is in the REPO SOURCE itself (missing Dockerfile
          referenced in azure.yaml, broken Bicep, corrupt lockfile that
          neither npm ci nor npm install can recover) AND a same-class
          attempt has already been tried, emit `give_up` with the reason
          PREFIXED by '[Escalate] '. The orchestrator will mark the session
          BlockedNeedsHumanOrSourceFix and surface a repo-source-fix proposal
          to the user instead of consuming more attempts.

        REPO LACKS DEPLOYMENT SCAFFOLDING -> ESCALATE, DO NOT INVENT
          If the failing step is `azd up` / `azd provision` / `azd deploy`
          / `azd env new` and the inspection shows NO `azure.yaml` at the
          repo root, OR the failing step is `terraform <x>` with NO `*.tf`
          present, OR the failing step references a Bicep file that does
          not exist on disk:
            -> EMIT `give_up` IMMEDIATELY with reasoning prefixed
               '[Escalate] ' explaining that the repository does not
               contain the required deployment artifacts.
          DO NOT invent commands such as `azd init --template-empty`,
          `azd init --from-code`, scaffolding a fresh `azure.yaml`,
          generating a Bicep file from scratch, or any other action that
          synthesizes deployment files the user did not author. The
          deploy must fail explicitly so the operator knows the source
          repo is not deployable as-is.

        YOU HAVE INSPECTION TOOLS � USE THEM BEFORE GUESSING:
          � read_workspace_file(relativePath)      � read up to 64 KB of a
                                                     repo file. Use BEFORE
                                                     proposing a sed that
                                                     patches it.
          � list_workspace_directory(path, glob)   � list up to 200 matches
                                                     for a glob. Use to
                                                     find hook scripts,
                                                     Dockerfiles, azure.yaml
                                                     in nested folders.
          � run_diagnostic(command)                � run a READ-ONLY
                                                     command (docker info,
                                                     az account show, azd
                                                     env get-values, ls,
                                                     cat, ...). Writes,
                                                     builds, 'azd up',
                                                     'az group delete',
                                                     pipes, redirections
                                                     are all rejected.
          � check_tool_available(tool)             � yes/no + version for
                                                     one of: docker, buildx,
                                                     az, azd, dotnet, node,
                                                     npm, python3, bicep,
                                                     terraform, git, jq.

        TOOL DISCIPLINE
          � PREFER inspection over speculation. When the error tail
            references azure.yaml / Dockerfile / a hook script, READ the
            file first, then propose a fix based on its ACTUAL contents.
          � Diagnostic commands are cheap and bounded (20 s, 16 KB
            stdout). Use them when the root cause is ambiguous.
          � After 2-3 tool calls you should have enough context to emit
            the final JSON remediation. Do not spin on diagnostics � if
            the failure class is already covered by the KNOWN FAILURES
            section below, apply the canonical fix directly.

        THREE OUTCOMES ARE POSSIBLE:
          � "replace_step"  � substitute the failing step with a corrected
                              command (same step id).
          � "insert_before" � add one or more preparatory steps BEFORE the
                              failing step (keep the failing step intact,
                              it will be retried after the preparation).
          � "give_up"       � the error is genuinely unrecoverable from
                              inside the sandbox (auth lost, quota exhausted,
                              Azure service outage, user-input required).

        COMMON PATTERNS YOU SHOULD RECOGNISE
          � exit code 127 + "cannot execute: required file not found" on a
            .sh path ? CRLF line endings in the shebang. Insert a prep step:
            bash -lc "find <repo> -name '*.sh' -exec sed -i 's/\r$//' {} +"
          � "ModuleNotFoundError" in Python ? insert pip install before
            the failing step.
          � "quota exceeded" for a SKU in one region ? replace the failing
            step with the same command after 'azd env set AZURE_X_LOCATION'
            to a different supported region.
          � TWO DISTINCT FAILURE MODES FOR MODELS � TREAT THEM DIFFERENTLY:

            A) "not found in the AI model catalog for <region>" / warning
               "model ... was not found in the AI model catalog" (model
               exists elsewhere, just not in THIS region)
               ? PREFER changing the region. Emit
                 'azd env set AZURE_LOCATION <region>' and, if the Scout
                 reports an AZURE_OPENAI_*_LOCATION var, set that too.
                 Pick a region from the "allowed value(s)" list in the
                 error when present.

            B) "ServiceModelDeprecated" / "model ... has been deprecated"
               (model is gone EVERYWHERE � no region change will help)
               ? RENAME the model and/or update the version. BEFORE
                 reaching for 'sed' on *.bicep, check the "AZD ENV VARS
                 BOUND BY THE TEMPLATE" block for any env var whose name
                 contains MODEL / VERSION / DEPLOYMENT (e.g.
                 AZURE_OPENAI_REALTIME_MODEL_VERSION=2024-12-17). If such
                 a var exists, THAT is the real source of truth � emit:
                   azd env set <VAR> <new-value>
                 alone. A 'sed' on the Bicep default is NOT ENOUGH in
                 this case because main.parameters.json overrides it.

                 Only when NO matching env var is listed should you fall
                 back to 'sed' on *.bicep AND *.parameters.json together:
                   find . -type f \( -name '*.bicep' -o -name '*.parameters.json' \) \
                     -exec sed -i 's/<old>/<new>/g' {} +

                 Pick the replacement name + version directly from the
                 AZURE OPENAI MODEL CATALOG block (same capability
                 family � audio stays audio, realtime stays realtime,
                 chat stays chat).

            C) "DeploymentModelNotSupported" / template validation refusal
               (model name/version unknown to the subscription at all)
               ? Apply the SAME env-var-first logic as rule B: if an
                 AZD ENV VAR for model / version exists, use
                 'azd env set' over 'sed'. Only when no var matches, try
                 region change first (catalog probe absent/empty) or
                 sed+parameters.json replacement (catalog populated but
                 the model is simply not in it).

            When the input includes an "AZURE OPENAI MODEL CATALOG" block,
            that is the AUTHORITATIVE list of models actually available
            in the user's subscription. PICK a replacement model name
            DIRECTLY from that list, never invent names from memory.

            CATALOG-PICKING RULES (read them every time):
              1. The "AZURE OPENAI MODEL CATALOG" block is organised by
                 region (look for '--- Region: <name> ---' headers). Up to
                 6 regions are probed: the one from the current plan plus
                 the historical "first release" regions for Azure OpenAI
                 (swedencentral, eastus2, westus3, northcentralus, eastus,
                 australiaeast).
              2. Rows listed are ALREADY filtered to remove models past
                 their deprecation date. You are SAFE picking any of them
                 � but see rule 3.
              3. Rows tagged "[DEPRECATING yyyy-MM-dd]" are valid today
                 but retiring within 90 days. Prefer a row WITHOUT this
                 tag if one exists in the same family.
              4. The NAME in the Bicep template must match the NAME column
                 exactly (e.g. write "gpt-realtime", not "gpt-realtime-v2").
                 The VERSION column goes into the model version field.
              5. If the failing repository references, say,
                 "gpt-4o-realtime-preview" and the catalog lists
                 "gpt-realtime", you must rewrite BOTH:
                   sed -i 's/gpt-4o-realtime-preview/gpt-realtime/g'
                 AND update the version string if the catalog version
                 differs from the repo one.

              6. REGION DECOUPLING (important for realtime / audio):
                 When the needed capability family is present ONLY in a
                 region different from AZURE_LOCATION, inspect the
                 Scout's "azd required env vars" list. If you see
                 'AZURE_OPENAI_SERVICE_LOCATION' (or any *_OPENAI_*_LOCATION
                 variant) in that list, the repo supports splitting the
                 OpenAI service region from the main deployment region.
                 Emit:
                   azd env set AZURE_OPENAI_SERVICE_LOCATION <region-with-model>
                 alongside the sed that renames the model. Do NOT change
                 AZURE_LOCATION unless the whole deployment must move.

              7. If NO row in any probed region matches the required
                 capability, STOP � emit "give_up" with a clear message
                 that the capability is not currently available in the
                 subscription across the probed regions.

            NEVER substitute a realtime model with a non-realtime one, an
            audio model with a non-audio one, or a chat model with an
            embedding model � capability class must be preserved, the
            application code depends on it.
          � "NameConflict" / "already exists" with deterministic Azure name
            ? replace with a unique suffix, or prepend a delete step.
          � "missing required inputs" with a named env var ? insert
            'azd env set <VAR> <value>'.
          � "context deadline exceeded" / flaky network ? replace with the
            same command wrapped in a short bash retry loop.

          � "InsufficientResourcesAvailable" / "out of the resources
            required to provision" (CAPACITY EXHAUSTED in the region)
            ? This is TRANSIENT at the service level, but the region does
              not currently have capacity for the specific SKU. DO NOT
              blindly switch region: check the REGION TRIAL HISTORY first.
              If the target service allows region decoupling (Scout
              mentions AZURE_<X>_LOCATION env vars), prefer moving ONLY
              that service to a different region, not AZURE_LOCATION.
              When AZURE_LOCATION itself must change AND a partial resource
              group already exists in the original region, you MUST first
              emit 'az group delete -n rg-<env-name> --yes --no-wait' in a
              prep step; otherwise the next 'azd up' will hit
              'InvalidResourceLocation: resource already exists in location X'.

          � "InvalidResourceLocation" / "already exists in location X. A
            resource with the same name cannot be created in location Y"
            (partial state from a cancelled / failed provision)
            ? You have TWO valid fixes, not three:
              a) STAY in location X (revert AZURE_LOCATION to X) � faster
                 if X is acceptable for the remaining resources.
              b) DELETE the resource group in X, THEN set AZURE_LOCATION=Y.
                 Emit two steps:
                   1. az group delete -n rg-<env-name> --yes --no-wait
                   2. azd env set AZURE_LOCATION <Y>
              NEVER propose option (b) without the az group delete first.

          � "InvalidTemplate" with "allowed value(s)" list
            ? The new AZURE_LOCATION MUST be a member of the BICEP
              TEMPLATE ALLOWED REGIONS list included in this input.
              Picking anything else is guaranteed to fail the same way.

          � OSCILLATION DETECTION � HARD STOP
            If the REGION TRIAL HISTORY shows you are about to propose a
            region whose combination of (region, error signature) is
            already listed there, STOP and emit "give_up" with a reason
            that explains the impasse to the user (typical phrasing:
            "Region R has capacity shortage and the only template-allowed
            alternatives already failed validation. This requires manual
            intervention: raise an Azure quota ticket or pick a different
            repo with a wider allowed-region list.").

        HARD RULES
          � Commands must start with: git, azd, az, pac, docker, dotnet,
            npm, node, python, pip, bash, sh, make, terraform, sed,
            find, chmod, dos2unix, tr.
            ('pwsh' is NOT installed in the sandbox � use 'bash -lc "..."'
            for shell substitutions like $(az account show --query id -o tsv).)
          � Never propose 'rm -rf /', 'curl | sh', remote exec, credential
            echo, or writes outside /workspace.
          � Use non-interactive flags (--no-prompt, --yes, -y).
          � Do NOT repeat a remediation that is listed under PREVIOUS
            ATTEMPTS. If you would just try the same thing again, output
            "give_up" with a clear reason instead.
          � If the PREVIOUS ATTEMPTS list is long (? 3 entries) and none of
            the existing approaches helped, strongly prefer "give_up" with
            a diagnostic reasoning over a new speculative attempt.
          � Each previous attempt is prefixed with its error signature in
            square brackets, e.g. 'step 5 [ServiceModelDeprecated]: ...'.
            IF THE SAME SIGNATURE APPEARS >= 3 TIMES CONSECUTIVELY, the
            current strategy class is not working: pivot to a different
            category of fix (region ? model rename; model rename ? region
            change; or give_up with a clear explanation that the problem
            is not self-healable from inside the sandbox).
          � Keep newSteps concise (1-3 steps usually suffice).

        DO NOT DEGRADE THE DEPLOY MODEL � the following are FORBIDDEN
        remediations because they make the deploy LOOK successful while
        actually leaving Azure with placeholder container images, empty
        ACRs, or half-provisioned services. The user sees "Succeeded"
        and only discovers the hollow deploy when opening the app:
          � DO NOT replace 'azd up' with 'azd provision' alone.
            'azd up' = package (build container images) + provision
            (create Azure resources) + deploy (push + activate).
            'azd provision' leaves Container Apps pointing at the
            default 'containerapps-helloworld:latest' placeholder and
            the ACR empty. If the build step fails, FIX THE BUILD
            STEP � do not skip it.
          � DO NOT set AZURE_SERVICE_<NAME>_RESOURCE_EXISTS=true to
            "get past" build failures. That flag exists for a narrow
            use case (re-running azd against a pre-deployed service
            you don't want touched). Setting it blanket silently
            skips docker build + image push + Container App update,
            producing the hello-world placeholder situation above.
          � DO NOT comment out the 'docker:' section from azure.yaml
            to sidestep the runtime check. For any azd template that
            declares a docker section, removing it means azd no longer
            knows how to produce the application image � a silent
            functional regression. Fix the container runtime instead.
          If the failure class is 'DockerRuntimeMissing' (see below)
          and the host daemon is truly unreachable, emit 'give_up'
          with a clear diagnostic � don't swap the deploy model.

        SANDBOX TOOLCHAIN � what's PRE-INSTALLED (do not try to install)
          � .NET SDK 8.0 and 9.0 (ready for 'dotnet restore', 'dotnet build',
            'dotnet user-secrets', and azd package hooks).
          � Node.js 20 LTS + npm.
          � Python 3 + pip + uv.
          � azd, az, git, bicep (via azd), terraform, make, jq, zip.
          � Docker CLI (client only) connected to the HOST daemon via a
            bind-mounted /var/run/docker.sock. 'docker build', 'docker
            push', 'docker run' all work and share the host's image cache.
            This is what lets 'azd up' on Container Apps / AKS repos with
            a local 'docker:' section succeed: the container runtime
            check passes and the build happens on the host daemon.
            DO NOT propose stubbing docker/podman binaries � it won't
            work (azd runs 'docker version' for real) AND it's not
            needed (docker IS there).
          � bash / sh (NO pwsh).
          If 'dotnet user-secrets' or 'dotnet restore' fails with 'SDK not
          found', the cause is NEVER a missing install � it is always a
          'global.json' pinning an SDK version (e.g. 10.0.100) not present
          in the sandbox. The FIX is ONE of:
             a) If the pin is >= 10, relax it:
                bash -lc "sed -i 's/\"version\": \"[0-9.]*\"/\"version\": \"8.0.100\"/' /workspace/global.json"
             b) If the pin is missing a 'rollForward' field, add one:
                bash -lc "jq '.sdk.rollForward=\"latestMajor\"' /workspace/global.json > /tmp/g && mv /tmp/g /workspace/global.json"
          DO NOT propose 'curl dotnet-install.sh' or writes under
          /workspace/.dotnet � the workspace bind mount is sometimes
          read-only / noexec on the host and the install will fail 126.
          DO NOT propose removing UserSecretsId from csproj files � the
          SDK is present, fix global.json instead.

        KNOWN FAILURE SIGNATURES ? CANONICAL FIXES
        (The orchestrator tags each previous attempt with [Signature]. If
         you see one of these, apply the canonical fix FIRST � do not
         invent new strategies.)

          [DotnetSdkNotFound]
            Cause: global.json pins SDK X, sandbox has only 8+9.
            Fix:   bash -lc "sed -i 's/\"version\": \"[0-9.]*\"/\"version\": \"8.0.100\"/' /workspace/global.json"
            DO NOT: curl dotnet-install.sh into /workspace.
            DO NOT: remove UserSecretsId from csproj.

          [WorkspacePermissionDenied]
            Cause: attempted to execute a binary under /workspace but the
                   host mount is noexec OR tried to write a readonly path.
            Fix:   move the operation to /tmp (writable+exec inside the
                   sandbox). Alternatively, use a pre-installed tool in
                   /usr/ (dotnet, node, npm, az, azd are all there).
            DO NOT: chmod or retry under /workspace � the mount flags are
                    set by the host and cannot be changed from inside.

          [DockerRuntimeMissing]
            Cause: azd reports "neither docker nor podman is installed"
                   OR "Cannot connect to the Docker daemon".
            Fix:   THE SANDBOX HAS DOCKER PRE-INSTALLED (agentichub/
                   sandbox:v9+) with /var/run/docker.sock bound to the
                   host. If this error appears the host daemon is DOWN �
                   not a repo/plan issue. Verify with a simple diagnostic:
                     bash -lc "docker version && docker info"
                   If the diagnostic also fails, emit 'give_up' with a
                   message asking the operator to start Docker Desktop.
            DO NOT: create docker/podman stub scripts � azd's LookPath
                    check invokes 'docker version' and will reject any
                    binary that doesn't match the real protocol.
            DO NOT: set GITHUB_ACTIONS=true � azd still runs the runtime
                    check in CI mode.
            DO NOT: edit azure.yaml to remove 'docker:' sections � the
                    repo's deploy model requires container builds; if
                    you can't do them here, the deploy is not feasible
                    from this environment.

          [BuildKitRequired]
            Cause: 'docker build' fails with "unknown flag: --mount"
                   or "Dockerfile parse error" around '--mount=type=...'
                   or BuildKit 'failed to solve' errors.
            Fix:   THE SANDBOX (v10+) SHIPS BUILDKIT ENABLED BY DEFAULT
                   (DOCKER_BUILDKIT=1) and the 'buildx' plugin. If you
                   still see this error, the env var may have been lost
                   by an azd hook wrapper � prepend it explicitly:
                     bash -lc "export DOCKER_BUILDKIT=1 && azd up --no-prompt"
                   OR tell the template to use buildx:
                     bash -lc "cd <service>; docker buildx build ."
                   OR (last resort) add the '# syntax=docker/dockerfile:1'
                   directive at top of the Dockerfile so the daemon picks
                   the BuildKit frontend regardless of the env var.
            DO NOT: strip '--mount=type=cache' / '--mount=type=secret'
                    from the Dockerfile with sed. Those directives are
                    legitimate BuildKit syntax; removing them breaks the
                    Dockerfile semantics (caches, secret injection) and
                    you'll end up in a loop trying to reconstruct what
                    you deleted.
            DO NOT: downgrade to 'docker build --legacy' � that flag is
                    not a real docker CLI option.

          [SignalKilled]
            Cause: the host OOM killer terminated 'az' (or occasionally
                   'node' / 'dotnet') mid-execution inside the sandbox.
                   Reported by azd as "AzureCLICredential: signal: killed"
                   or generic "signal: killed" in the tail. This is a
                   TRANSIENT resource event, NOT a logic error � every
                   environmental condition is unchanged on the next
                   attempt. The sandbox runs with 6 GB / 10 GB swap by
                   default, so a retry almost always succeeds.
            Fix:   Emit 'replace_step' with EXACTLY the same command
                   (no edits, no --debug flag additions, no env mutations)
                   so the orchestrator retries the step after a brief
                   cool-off. If you see the SAME [SignalKilled] signature
                   twice in a row in PREVIOUS ATTEMPTS, escalate: insert
                   a prep step that frees buildx build cache to reduce
                   memory pressure before the next retry:
                     bash -lc "docker buildx prune -f --keep-storage 500MB"
                   If still failing after 3 total attempts emit give_up
                   with a message asking the operator to raise Docker
                   Desktop's memory allocation (Settings ? Resources ?
                   Memory ? 12 GB minimum).
            DO NOT: rewrite the command with unrelated changes � the
                    retry must be pristine so the signature correlation
                    stays meaningful on the next attempt.
            DO NOT: add 'timeout N' / 'ulimit' wrappers � the issue is
                    the host-level OOM killer, not an internal limit.

          [StepSilent]
            Cause: the orchestrator aborted the step because it went
                   silent for >= 15 min (no stdout/stderr) while still
                   running. The classic trigger is 'docker buildx build'
                   stuck inside 'npm install' in a Node/Angular Dockerfile
                   that can't reach a registry, or 'azd deploy' waiting on
                   a revision that will never become healthy.
            Fix:   BYPASS THE LOCAL BUILD. Switch the affected service to
                   an ACR remote build + direct containerapp update �
                   builds run on Azure infra with stable network, no
                   local docker dependency, typically complete in 3-5 min.

                   Autonomous recipe � emit TWO 'insert_before' prep
                   steps targeting the failing step, then keep the
                   failing step for the final retry:

                   Step A  (ACR build, cloud-side):
                     bash -lc "cd /workspace/<path-to-service> && \
                       az acr build \
                         --registry $(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | cut -d= -f2 | tr -d '\"') \
                         --image <service>:azd-$(date +%s) \
                         --file Dockerfile . --no-logs"

                   Step B  (promote new image to the Container App):
                     bash -lc "az containerapp update \
                       -n <service> \
                       -g $(azd env get-values | grep AZURE_RESOURCE_GROUP | cut -d= -f2 | tr -d '\"') \
                       --image $(az acr repository show-tags \
                           -n $(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | cut -d= -f2 | tr -d '\"') \
                           --repository <service> --orderby time_desc --top 1 -o tsv)"

                   Identify <service> from the failing step output: the
                   tail usually contains 'Deploying service <service>' or
                   'Building image for <service>'. Use 'list_workspace_
                   directory(".", "**/Dockerfile")' to locate <path-to-
                   service>. Use 'azd env set AZURE_SERVICE_<SERVICE_UP>_
                   RESOURCE_EXISTS true' ONLY as a TEMPORARY flag AFTER
                   the ACR build succeeds so a subsequent 'azd deploy'
                   won't re-run the hang � remove the flag on next
                   successful full deploy.

            DO NOT: simply retry 'azd deploy <service>' without
                    switching strategy � buildx will hang the same way.
            DO NOT: cancel the whole deploy. The previously-deployed
                    services are live; surgical recovery on the
                    silent service is enough.

          [ContainerAppValidationTimeout]
            Cause: Azure Container Apps control plane reports
                     "Validation of container app creation/update
                      timed out, which might be caused by required
                      network access being blocked"
                   for one or more Container Apps during a parallel
                   Bicep deployment. 8-service monorepos hit this
                   regularly when ACA validates every revision at
                   once and some slip past the internal budget.
            Fix:   The orchestrator AUTO-RETRIED this step up to 3
                   times before calling you (see AUTO_RETRY entries
                   in PREVIOUS ATTEMPTS). If you're invoked, 3
                   consecutive retries all failed � the issue is
                   no longer just a region-capacity blip. Serialise
                   the creation so the recreate happens without
                   contention from the already-Succeeded Container
                   Apps:

                   Step A  (delete ONLY the Failed Container Apps
                   so the next provision recreates just those):
                     bash -lc "rg=$(azd env get-values | grep AZURE_RESOURCE_GROUP | cut -d= -f2 | tr -d '\"'); \
                       for app in $(az containerapp list -g \"$rg\" --query '[?properties.provisioningState==\\`Failed\\`].name' -o tsv); do \
                         echo \"Deleting failed Container App: $app\"; \
                         az containerapp delete -n \"$app\" -g \"$rg\" --yes --only-show-errors; \
                       done"

                   Step B  (re-run azd up � idempotent, only the
                   deleted Container Apps are recreated, and with
                   fewer concurrent operations Azure's validation
                   almost always succeeds):
                     bash -lc "cd /workspace && azd up --no-prompt"

            DO NOT: delete the whole resource group � that destroys
                    the Succeeded Container Apps too and triggers an
                    even bigger validation burst on the next provision.
            DO NOT: pass a '--concurrency' flag; Bicep doesn't honour
                    a subscription-scope concurrency knob.

          [AzdStateStale]
            Cause: 'azd deploy' fails with
                     "unable to find a resource tagged with
                      'azd-service-name: <service>'"
                   AND 'azd provision' earlier said
                     "Skipped: Didn't find new changes." / "SUCCESS:
                      There are no changes to provision".
                   azd's LOCAL state cache (.azure/<env>/) believes
                   the infrastructure is deployed, but the REAL Azure
                   state is empty or missing the tagged Container Apps.
                   Typically happens after a partial failure or after
                   the user / an earlier remediation deleted the
                   resource group out-of-band. Deleting the RG AGAIN
                   does NOT help: azd's skip-logic fires on the next
                   provision and deploy fails the same way. If you see
                   this signature in PREVIOUS ATTEMPTS twice in a row,
                   you ARE in the loop � break it with the recipe below.
            Fix:   Bypass azd's skip logic by driving Bicep directly
                   via 'az deployment sub create', then run 'azd deploy':

                   Step A  (force a fresh ARM deployment that actually
                   runs the Bicep and creates tagged resources):
                     bash -lc "cd /workspace && \
                       env_name=$(azd env get-values | grep AZURE_ENV_NAME | cut -d= -f2 | tr -d '\"'); \
                       loc=$(azd env get-values | grep AZURE_LOCATION | cut -d= -f2 | tr -d '\"'); \
                       az deployment sub create \
                         --name azd-force-$(date +%s) \
                         --location $loc \
                         --template-file infra/main.bicep \
                         --parameters environmentName=$env_name location=$loc \
                         --only-show-errors"

                   Step B  (sync azd local cache from the real Azure
                   state so subsequent azd commands see the resources):
                     bash -lc "cd /workspace && azd env refresh --no-prompt"

                   Step C  (deploy the application images now that
                   tagged Container Apps exist):
                     bash -lc "cd /workspace && azd deploy --no-prompt"

            DO NOT: emit yet another 'delete RG + azd provision + azd
                    deploy' variation. You've already tried that
                    shape multiple times in this session (check
                    PREVIOUS ATTEMPTS) and it loops because 'azd
                    provision' short-circuits on cached state. Only
                    the A+B+C sequence above breaks the loop.
            DO NOT: use 'azd provision --force' � that flag does not
                    exist in azd and produces 'unknown flag: --force'.
            DO NOT: use 'az group exists -n $rg' without quoting �
                    an empty $rg produces "expected one argument".

          [ExecFormatError]
            Cause: downloaded a binary for the wrong architecture
                   (amd64 bin on arm64 or vice versa).
            Fix:   if the repo has an install.sh, pass the target arch
                   flag; else pull the matching arch tarball. The
                   sandbox 'uname -m' reports the true architecture.

          [NoexecBindMount]
            Cause: /workspace is a Windows bind-mount (Docker Desktop +
                   WSL2) that does NOT propagate the executable bit for
                   files created from inside the container. Typical
                   symptoms in the error tail:
                     � "spawnSync /workspace/**/node_modules/**/bin/<x>
                        EACCES" (esbuild, rollup, tsc, prettier after
                        `npm install`),
                     � "/workspace/**/*.sh: Permission denied" when a
                        hook script calls `./script.sh`,
                     � "cannot execute: required file not found" on an
                        extracted tarball binary under /workspace.
                   Running `chmod +x` does NOT help: the bit is stripped
                   by the mount driver, not missing in the file.
            Fix:   The --ignore-scripts workaround ONLY masks install-
                   time failures. At runtime (`ng build`, `vite build`,
                   `next build`) node will call the same binary and hit
                   the same EACCES. The DEFINITIVE fix is to relocate
                   the affected directory to /tmp (tmpfs, exec-capable
                   inside the sandbox) via symlink BEFORE the build:

                   Single service recipe (when the failing binary is
                   in one package's node_modules):
                     bash -lc "set -e; \
                       svc=/workspace/packages/ui-angular; \
                       mkdir -p /tmp/nm-ui-angular; \
                       rm -rf \"$svc/node_modules\"; \
                       ln -sfn /tmp/nm-ui-angular \"$svc/node_modules\"; \
                       cd \"$svc\" && npm ci"

                   For hook scripts (postprovision.sh, preprovision.sh
                   calling sub-scripts), ALSO patch every bare `./X.sh`
                   or `bash X.sh` invocation to go through `sh`
                   explicitly (sh reads + executes the script as text,
                   bypassing the exec-bit requirement):
                     bash -lc "sed -i -E 's|(^|\\s)(\\./[A-Za-z0-9_./-]+\\.sh)|\\1sh \\2|g' \
                       infra/hooks/postprovision.sh"

                   For tarball-extracted CLI tools, copy the binary to
                   /usr/local/bin (writable exec path) before the step
                   that invokes it:
                     bash -lc "install -m 0755 /workspace/dist/<tool> \
                       /usr/local/bin/<tool>"
            DO NOT: chmod +x under /workspace � the bind mount ignores
                    it. The next npm operation will recreate the file
                    with the same stripped bit.
            DO NOT: stop at `--ignore-scripts`. It only delays the
                    failure until the build step, producing a harder-
                    to-diagnose loop two steps later.
            DO NOT: propose a sandbox image rebuild � the issue is at
                    the mount level, not in the image.

          [CommandNotFound]
            Cause: the step references a binary not in PATH.
            Fix:   if it is in the PRE-INSTALLED list above, the command
                   is probably missing a 'bash -lc "..."' wrapper or
                   typoed. Else install it with tdnf / apt (when writable)
                   or pick a different tool.

          [BicepTemplateError]
            Cause: Bicep validation failed. Look at the inner error:
                   - 'not in the allowed value(s)' ? azd env set <VAR>
                     to one of the allowed values (prefer env set over
                     editing *.bicep � see AZD ENV VARS BOUND block).
                   - 'already exists in location X' ? az group delete
                     the stale RG BEFORE retrying.

          [ServiceModelDeprecated]
            Cause: the OpenAI model/version is past its sunset date.
            Fix:   use the AzureCatalogProbe output to pick a live model
                   + its native region, then 'azd env set' for both the
                   model name and the region.

          [InvalidResourceLocation]
            Cause: the RG already has resources in location A, but the
                   plan now provisions in location B.
            Fix:   az group delete -n <rg> --yes (no --no-wait so the
                   next step waits for completion).

        IMPORTANT: THERE IS NO HARD BUDGET ON REMEDIATION ATTEMPTS
          The orchestrator will keep calling you as long as you propose a
          new actionable fix. You are the primary loop terminator: choose
          "give_up" deliberately when further autonomous work would just
          burn quota / cloud resources without solving the problem. The
          final line of defence is the session budget (e.g. 90 min), but
          reaching it produces a confusing "cancelled" state rather than a
          clean diagnostic � so do not rely on it.

        WHEN STDERR IS EMPTY � DIAGNOSE BEFORE GUESSING
          Some failures have NO visible error detail in the step tail.
          The most common offenders are:
            � azd pre/post hooks declared in azure.yaml that invoke a
              script which fails silently.
            � 'dotnet build' inside an MSBuild task that swallows output.
            � Bicep modules whose deployment errors only surface in the
              Azure portal 'Deployments' blade.
          When you see "hook failed with exit code N, stderr: " (empty),
          DO NOT guess at CRLF / chmod / 'set -e' fixes without evidence.
          INSTEAD, emit a DIAGNOSTIC step FIRST that reveals the hook:
            {
              "id": <next>,
              "description": "Show azure.yaml hooks + the failing script content",
              "cmd": "bash -lc \"cat /workspace/azure.yaml; echo '--- hooks below ---'; find /workspace -maxdepth 3 -name '*preprovision*' -o -name '*postprovision*' -o -name '*prepackage*' | while read f; do echo \\\"==> $f <==\\\"; cat \\\"$f\\\"; done\"",
              "cwd": "."
            }
          The next orchestrator failure log will include the script body,
          at which point you can propose a targeted fix rather than
          spinning through speculative patches.

        OUTPUT � respond ONLY with a JSON object:
        {
          "kind": "replace_step" | "insert_before" | "give_up",
          "stepId": <int>,
          "newSteps": [
            { "id": <int>, "description": "...", "cmd": "...", "cwd": "." }
            // OR replace "cmd" with a typed "action" object (preferred
            //    for ACR builds, container app updates, azd env set):
            // { "id": <int>, "description": "...", "action": { "type":"AcrBuild", ... }, "cwd": "." }
          ],
          "reasoning": "one or two sentences, no markdown"
        }

        TYPED ACTIONS � USE THESE FIRST when the operation matches.
        Inline shell pipelines like
        `REG=$(azd env get-values | grep AZURE_X | cut -d= -f2 | tr -d '"')`
        are FORBIDDEN for the operations below. They burn the entire
        remediation budget on shape-of-pipeline variations
        (`grep|cut` -> `sed -n` -> `awk -F=`) of an extraction the
        orchestrator already does once and exposes via DeployContext.

        a) Build a Docker image on Azure Container Registry remote
           build (use this whenever a local docker build hangs or
           when --registry would need to be filled from azd):
             "action": {
               "type": "AcrBuild",
               "service": "ui-angular",
               "contextDir": "packages/ui-angular",
               "dockerfile": "Dockerfile.production",
               "imageName": "ui-angular"
             }
           Registry, login server, tag are all resolved by the
           orchestrator from DeployContext (post-azd-provision).
           Do NOT pass --registry, --image, or any tag yourself.

        b) Update an existing Container App to the most recently
           AcrBuilt image for the same service:
             "action": {
               "type": "ContainerAppUpdate",
               "service": "ui-angular",
               "imageRef": "$LASTBUILT"
             }
           Resource group is resolved from DeployContext.

        c) Set a value in the azd environment without inline shell
           substitution:
             "action": { "type": "AzdEnvSet", "key": "AZURE_SUBSCRIPTION_ID",
                         "valueFrom": "AzAccountSubscriptionId" }
           valueFrom � { "AzAccountSubscriptionId" | "AzAccountTenantId" }
           or a literal "value": "...".

        d) Pure file edits or one-shot shell tweaks fall back to
           "cmd" or to the explicit Bash action:
             "action": { "type": "Bash",
                         "script": "sed -i 's/capacity: 50/capacity: 1/g' infra/main.bicep" }

        KIND SEMANTICS � read carefully, this is the single biggest source
        of silent deploy bugs when misunderstood:

          � "replace_step" replaces the step whose id equals 'stepId' in
            the current plan with 'newSteps'. You may ONLY replace:
              (a) the CURRENTLY FAILING step (the one in FAILED STEP
                  above). Use this to fix the step itself � e.g. rewriting
                  a malformed command.
              (b) a FUTURE step that has not yet executed.
            NEVER use replace_step on a step that already ran successfully:
            the system cannot un-execute history. If you set
            AZURE_LOCATION=eastus in step 4 and now want it to be eastus2,
            DO NOT say 'replace_step stepId=4 with azd env set
            AZURE_LOCATION eastus2'. Step 4 is in the past. Use
            'insert_before' with stepId = the failing step instead.

          � "insert_before" inserts 'newSteps' immediately BEFORE the step
            whose id equals 'stepId' (typically the currently failing
            one). Use this for corrective setup that must run before the
            failing action is retried � e.g. prepending
            'azd env set AZURE_LOCATION eastus2' before the failing
            'azd up'. This is the correct tool 90% of the time.

          � "give_up" abandons remediation with a clear user-facing reason
            in 'reasoning'.

        RULE OF THUMB: if in doubt between replace_step and insert_before,
        use insert_before with stepId = FAILED STEP id. insert_before is
        always safe; replace_step on the wrong target drops real work.

        STRICTLY INTEGER IDs
        - Every 'id' and 'stepId' MUST be a whole integer (1, 2, 42). Never
          9.5 or "10a" or "step-3". Use any integer value � the orchestrator
          renumbers the list after applying the remediation, so you do not
          need to maintain ordering yourself.
        """;

    /// <summary>
    /// Renders the cross-session insights as a leading "PRIOR LEARNINGS"
    /// block in the agent prompt. Filtered to medium+ confidence so noisy,
    /// speculative entries don't drown out the well-established ones. The
    /// Strategist and the Doctor are told these are HINTS, not orders �
    /// they should still verify against the current run's evidence.
    /// </summary>
    private static void AppendPriorInsights(StringBuilder sb, IReadOnlyList<PriorInsightDto>? priorInsights)
    {
        if (priorInsights is null || priorInsights.Count == 0) return;

        var ranked = priorInsights
            .Where(i => i.Confidence >= 0.5)
            .OrderByDescending(i => i.Confidence)
            .Take(15)
            .ToList();
        if (ranked.Count == 0) return;

        sb.AppendLine("PRIOR LEARNINGS (from previous deploys of this repo / global memory):");
        sb.AppendLine("Treat these as HINTS, not orders. Verify against the current run's");
        sb.AppendLine("evidence. Higher 'conf' = more reliable. Older entries may be stale");
        sb.AppendLine("if Microsoft has rotated services in between.");
        foreach (var i in ranked)
            sb.AppendLine($"  - [{i.Key}] (conf={i.Confidence:0.0}, at={i.At}): {i.Value}");
        sb.AppendLine();
    }

    private static string BuildDoctorInput(
        RepoInspector.ToolchainManifest m,
        DeploymentPlanDto plan,
        int failedStepId,
        string errorTail,
        IReadOnlyList<string> previousAttempts,
        string? azureCatalogSnapshot,
        IReadOnlyList<PriorInsightDto>? priorInsights)
    {
        var sb = new StringBuilder();
        AppendPriorInsights(sb, priorInsights);
        sb.AppendLine("FAILED STEP:");
        var failedStep = plan.Steps.FirstOrDefault(s => s.Id == failedStepId);
        if (failedStep is not null)
        {
            sb.AppendLine($"  id: {failedStep.Id}");
            sb.AppendLine($"  description: {failedStep.Description}");
            sb.AppendLine($"  cmd: {failedStep.Command}");
            sb.AppendLine($"  cwd: {failedStep.WorkingDirectory}");
        }
        sb.AppendLine();
        sb.AppendLine("ERROR OUTPUT (last ~2KB):");
        sb.AppendLine(errorTail.Length > 2000 ? errorTail[^2000..] : errorTail);
        sb.AppendLine();
        sb.AppendLine("FULL PLAN:");
        foreach (var s in plan.Steps)
            sb.AppendLine($"  [{s.Id}] {s.Description} � `{s.Command}` (cwd: {s.WorkingDirectory})");
        sb.AppendLine();
        sb.AppendLine("TOOLCHAIN MANIFEST:");
        sb.AppendLine("  " + string.Join(", ", m.Summary()));
        if (m.Rationale.Count > 0)
        {
            sb.AppendLine("  file locations:");
            foreach (var r in m.Rationale) sb.AppendLine("    " + r);
        }

        // The full list of env vars the Bicep reads (including those with
        // defaults). This is the KEY to avoiding "sed on *.bicep didn't
        // actually change anything because the value comes from
        // main.parameters.json" loops.
        if (m.AzdAllEnvVars.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("AZD ENV VARS BOUND BY THE TEMPLATE (name=current_default):");
            sb.AppendLine("  If you need to change the VALUE of any of these, prefer");
            sb.AppendLine("  'azd env set <VAR> <new-value>' OVER 'sed' on Bicep �");
            sb.AppendLine("  main.parameters.json overrides the Bicep default at deploy time,");
            sb.AppendLine("  so sed on *.bicep alone has no effect for these vars.");
            foreach (var v in m.AzdAllEnvVars) sb.AppendLine("  - " + v);
        }

        // .NET SDK pin awareness. If the repo's global.json pins an SDK
        // major the sandbox image doesn't carry, we announce it loudly
        // here so the Doctor doesn't try to 'curl dotnet-install.sh'
        // into /workspace/.dotnet (which fails 126 � noexec mount).
        // The canonical fix for this class of failure is 'sed' on
        // global.json to drop the pin to 8.0.100.
        if (!m.DotnetSdkPinSatisfiable && m.DotnetSdkPin is not null)
        {
            sb.AppendLine();
            sb.AppendLine("DOTNET SDK PIN MISMATCH (do NOT try to install an SDK at deploy time):");
            sb.AppendLine($"  global.json pins SDK '{m.DotnetSdkPin}' (major {m.DotnetSdkPinMajor}).");
            sb.AppendLine("  Sandbox image provides SDK 8 + SDK 9 ONLY.");
            sb.AppendLine("  CANONICAL FIX when 'dotnet user-secrets' / 'dotnet restore' fails:");
            sb.AppendLine("    bash -lc \"sed -i 's/\\\"version\\\": \\\"[0-9.]*\\\"/\\\"version\\\": \\\"8.0.100\\\"/' /workspace/global.json\"");
            sb.AppendLine("  NEVER try to download dotnet-install.sh to /workspace � the mount");
            sb.AppendLine("  is often read-only or noexec on arm64 Docker Desktop. Installing");
            sb.AppendLine("  into /tmp works for THIS invocation only, then evaporates; azd");
            sb.AppendLine("  spawns children that don't see it.");
        }

        if (previousAttempts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("PREVIOUS ATTEMPTS (do NOT repeat these):");
            foreach (var p in previousAttempts) sb.AppendLine("  - " + p);
        }

        // ---- Region intelligence: help the Doctor avoid oscillation ----
        // Two common patterns cause the Doctor to ping-pong:
        // 1. It picks region X (validation-allowed), capacity exhausted
        //    -> moves to region Y (not in Bicep allowed list) -> validation
        //    rejects Y -> back to X -> still exhausted -> ...
        // 2. It moves region mid-deploy after resources exist -> 'already
        //    exists in location X' error -> reverts -> capacity again ->
        //    reverts -> ...
        // Giving the Doctor a STRUCTURED snapshot of (a) which regions the
        // template allows, (b) which regions it already tried with which
        // error signature, breaks the loop.
        var allowedRegions = ExtractAllowedRegionsFromError(errorTail);
        if (allowedRegions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("BICEP TEMPLATE ALLOWED REGIONS (from an InvalidTemplate error in history):");
            sb.AppendLine("  Any region picked for AZURE_LOCATION MUST come from this list.");
            sb.AppendLine("  " + string.Join(", ", allowedRegions));
        }

        var regionTrials = BuildRegionTrialTable(previousAttempts);
        if (regionTrials.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REGION TRIAL HISTORY (region value -> observed error signatures):");
            foreach (var (region, sigs) in regionTrials)
                sb.AppendLine($"  - {region} : {string.Join(" + ", sigs)}");
            sb.AppendLine("  Do NOT pick a region already listed here unless the error signature");
            sb.AppendLine("  was transient (e.g. network). InsufficientResourcesAvailable,");
            sb.AppendLine("  NameConflict and InvalidTemplate do NOT clear themselves on retry.");
        }

        if (!string.IsNullOrEmpty(azureCatalogSnapshot))
        {
            sb.AppendLine();
            sb.AppendLine("AZURE OPENAI MODEL CATALOG (live from the user's subscription):");
            sb.AppendLine("Use ONLY models listed here. Do not invent names you remember");
            sb.AppendLine("from training data � those may be deprecated or unavailable.");
            sb.AppendLine(azureCatalogSnapshot);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses the Bicep "allowed value(s)" list out of an InvalidTemplate
    /// validation error. The typical signature is:
    ///   'The value 'X' is not part of the allowed value(s):
    ///     'brazilsouth,canadacentral,eastus2,...'.'
    /// Giving the Doctor this explicit set is the cleanest way to prevent
    /// it from proposing a region the template will reject on validation.
    /// </summary>
    private static List<string> ExtractAllowedRegionsFromError(string errorTail)
    {
        if (string.IsNullOrEmpty(errorTail)) return new();
        var m = System.Text.RegularExpressions.Regex.Match(
            errorTail,
            @"allowed value\(s\):\s*'([^']+)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return new();

        return m.Groups[1].Value
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim().ToLowerInvariant())
            .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r, @"^[a-z][a-z0-9]{2,}$"))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Aggregates the previousAttempts strings into a map
    /// 'region-value -> [error signatures observed]'. Each entry in
    /// previousAttempts currently has the shape:
    ///     'step N [ErrorSignature]: {cmd} -> {kind} -> {newCmd}'
    /// and the {newCmd} often contains 'AZURE_LOCATION &lt;region&gt;'.
    /// The pairing lets the Doctor see at a glance which regions have
    /// been burned already and why, so it stops proposing them.
    /// </summary>
    private static List<(string region, List<string> signatures)> BuildRegionTrialTable(
        IReadOnlyList<string> previousAttempts)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in previousAttempts)
        {
            // Extract signature in square brackets.
            var sigMatch = System.Text.RegularExpressions.Regex.Match(line, @"\[([^\]]+)\]");
            var sig = sigMatch.Success ? sigMatch.Groups[1].Value : "unknown";

            // Find every 'AZURE_LOCATION <region>' token.
            var regionMatches = System.Text.RegularExpressions.Regex.Matches(
                line,
                @"AZURE_LOCATION\s+([a-z][a-z0-9]{2,})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match rm in regionMatches)
            {
                var region = rm.Groups[1].Value.ToLowerInvariant();
                if (!map.TryGetValue(region, out var sigs))
                    map[region] = sigs = new List<string>();
                if (!sigs.Contains(sig))
                    sigs.Add(sig);
            }
        }
        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// When the error suggests a model-catalog mismatch, shell out to
    /// 'az cognitiveservices model list' against the planned region to
    /// retrieve the REAL set of deployable OpenAI models in the user's
    /// subscription. Without this snapshot the Doctor ends up guessing
    /// plausible-sounding names like 'gpt-4o-realtime' or 'gpt-realtime'
    /// that may not exist in the tenant, burning one remediation attempt
    /// per guess.
    /// </summary>
    private async Task<string?> ProbeAzureModelCatalogAsync(
        string errorTail, DeploymentPlanDto plan, CancellationToken ct)
    {
        // Only probe when we clearly have a model-related failure; pure
        // quota / auth / network errors don't benefit from a model list.
        var normalizedError = errorTail?.ToLowerInvariant() ?? "";
        var isModelError =
            normalizedError.Contains("modelnotsupported") ||
            normalizedError.Contains("deploymentmodelnotsupported") ||
            normalizedError.Contains("not found in the ai model catalog") ||
            (normalizedError.Contains("model") && normalizedError.Contains("deprecated")) ||
            (normalizedError.Contains("model") && normalizedError.Contains("not supported"));
        if (!isModelError) return null;

        // Try to infer the probe region: the plan usually carries an
        // 'azd env set AZURE_LOCATION <region>' step that we can grep.
        // Fallback to 'eastus2' which has the broadest coverage for
        // realtime + chat frontier models at the time of writing.
        var primaryRegion = ExtractLocationFromPlan(plan) ?? "eastus2";

        // Multi-region probe: if the primary region lacks the capability
        // family we need (e.g. realtime), a second region that does have
        // it lets the Doctor propose 'azd env set AZURE_OPENAI_SERVICE_
        // LOCATION <region>' � the Scout already flagged that env var as
        // supported by the repo, so only the OpenAI service moves while
        // the rest of the infra stays in AZURE_LOCATION.
        //
        // The list is curated: these are the regions that historically
        // get new Azure OpenAI model releases first. Querying all of
        // Azure's 60+ regions would be prohibitively slow; this subset
        // covers the realistic deploy targets.
        var fallbackRegions = new[]
        {
            "swedencentral", "eastus2", "westus3",
            "northcentralus", "eastus", "australiaeast"
        };
        var regionsToProbe = new[] { primaryRegion }
            .Concat(fallbackRegions.Where(r => !string.Equals(r, primaryRegion, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var perRegionSnapshots = new List<(string region, string formatted, bool hasFamilyMatch)>();
        foreach (var region in regionsToProbe)
        {
            if (ct.IsCancellationRequested) break;
            var snapshot = await ProbeSingleRegionAsync(region, errorTail, ct);
            if (snapshot is null) continue;
            perRegionSnapshots.Add((region, snapshot.Value.formatted, snapshot.Value.hasFamilyMatch));

            // Optimisation: as soon as we find a region with live family
            // matches, we still probe 1-2 more to give the Doctor options,
            // but stop after 3 positive hits to keep latency bounded.
            if (perRegionSnapshots.Count(s => s.hasFamilyMatch) >= 3) break;
        }

        if (perRegionSnapshots.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var (region, formatted, _) in perRegionSnapshots)
        {
            sb.AppendLine($"--- Region: {region} ---");
            sb.AppendLine(formatted);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Runs 'az cognitiveservices model list' against a single region and
    /// returns the formatted table plus a flag indicating whether any row
    /// matched the failing model's family. The flag lets the caller stop
    /// early once enough viable regions have been found.
    /// </summary>
    private static async Task<(string formatted, bool hasFamilyMatch)?> ProbeSingleRegionAsync(
        string region, string errorTail, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments =
                    "cognitiveservices model list " +
                    $"--location {region} " +
                    "--query \"[?kind=='OpenAI'].{name:model.name, version:model.version, skus:model.skus[].name, deprecation:model.deprecation.inference, lifecycleStatus:model.lifecycleStatus}\" " +
                    "-o json --only-show-errors",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return null; }

            if (proc.ExitCode != 0) return null;
            var json = await proc.StandardOutput.ReadToEndAsync(ct);
            return FormatCatalog(json, errorTail);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses 'az cognitiveservices model list' output and formats it as
    /// a compact human-readable table. Filters out models whose
    /// deprecation date is in the past (the #1 cause of the Doctor's
    /// ping-pong between deprecated models), tags models deprecating
    /// within 90 days as "(DEPRECATING soon)", and prioritises rows that
    /// share a family with the failing model referenced in the errorTail
    /// so the Doctor does not drown in 200+ catalog rows.
    /// </summary>
    private static (string formatted, bool hasFamilyMatch) FormatCatalog(string json, string errorTail)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ("", false);

            // Extract a hint about which model family failed, if any. The
            // classic signature is: model 'Format:OpenAI,Name:<name>,Version:<v>'.
            var familyHint = System.Text.RegularExpressions.Regex
                .Match(errorTail, @"Name:([A-Za-z0-9_\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Groups[1].Value;
            if (string.IsNullOrEmpty(familyHint))
            {
                familyHint = System.Text.RegularExpressions.Regex
                    .Match(errorTail, @"model\s+[""']([A-Za-z0-9_\-]+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    .Groups[1].Value;
            }

            // Split family: 'gpt-4o-realtime-preview' -> ['gpt', '4o', 'realtime'].
            // 'preview' is intentionally kept in the token set because the
            // family token match already ignores short tokens; 'preview'
            // scoring helps PREFER other -preview entries when the failing
            // model itself is a preview, without polluting the filter.
            var familyTokens = (familyHint ?? "")
                .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2 && !t.Equals("preview", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.ToLowerInvariant())
                .ToArray();

            var now = DateTimeOffset.UtcNow;
            var soon = now.AddDays(90);
            var rows = new List<(string name, string version, string skus, int score, string status, DateTimeOffset? dep)>();
            int totalRaw = 0, droppedDeprecated = 0;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                totalRaw++;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var ver  = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;

                // Parse deprecation date (null / absent means not deprecated).
                DateTimeOffset? deprecation = null;
                if (item.TryGetProperty("deprecation", out var dep) && dep.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(dep.GetString(), out var parsed))
                        deprecation = parsed;
                }

                // Drop models already past their deprecation date: exposing
                // them to the Doctor caused endless substitutions between
                // one deprecated model and another (gpt-4o-realtime-preview
                // -> gpt-4o-audio-preview -> both deprecated).
                if (deprecation is { } d && d <= now)
                {
                    droppedDeprecated++;
                    continue;
                }

                string skus = "";
                if (item.TryGetProperty("skus", out var s) && s.ValueKind == JsonValueKind.Array)
                    skus = string.Join("/", s.EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .Where(x => !string.IsNullOrEmpty(x))
                        .Distinct());

                // Status tag: flag imminent deprecations so the Doctor
                // avoids them when a longer-lived alternative exists.
                string status = "";
                if (deprecation is { } d2)
                {
                    if (d2 <= soon) status = $"DEPRECATING {d2:yyyy-MM-dd}";
                    else status = $"deprecates {d2:yyyy-MM-dd}";
                }

                // Score by how many family tokens appear in the model name.
                var lowerName = name.ToLowerInvariant();
                int score = familyTokens.Count(t => lowerName.Contains(t));
                rows.Add((name, ver, skus, score, status, deprecation));
            }

            // If at least one row scored > 0, show only family-matching rows
            // (typically 5-15). Otherwise show the first 50 as a broad view.
            // Within family matches, prefer non-deprecating rows first.
            var prioritized = (rows.Any(r => r.score > 0)
                ? rows.Where(r => r.score > 0)
                : rows.Take(80))
                .OrderByDescending(r => r.score)
                .ThenBy(r => r.dep.HasValue ? 1 : 0) // non-deprecating first
                .ThenByDescending(r => r.dep ?? DateTimeOffset.MaxValue)
                .ThenBy(r => r.name)
                .Take(40)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"  (showing {prioritized.Count} live models, filtered out {droppedDeprecated} already-deprecated of {totalRaw} total"
                + (familyTokens.Length > 0 ? $"; family filter: '{familyHint}')" : ")"));
            foreach (var r in prioritized)
            {
                var tag = string.IsNullOrEmpty(r.status) ? "" : $"  [{r.status}]";
                sb.AppendLine($"  - {r.name}  version={r.version}  skus={r.skus}{tag}");
            }
            sb.AppendLine();
            sb.AppendLine("  Rules for picking a replacement:");
            sb.AppendLine("  1. All rows above are LIVE � already-deprecated models were removed.");
            sb.AppendLine("  2. Prefer rows WITHOUT a [DEPRECATING yyyy-MM-dd] tag.");
            sb.AppendLine("  3. Stay in the same capability family (realtime -> realtime, audio -> audio, chat -> chat).");
            return (sb.ToString(), hasFamilyMatch: rows.Any(r => r.score > 0));
        }
        catch
        {
            // Return the raw JSON truncated, better than nothing.
            return (json.Length > 3000 ? json[..3000] + "..." : json, false);
        }
    }

    private static string? ExtractLocationFromPlan(DeploymentPlanDto plan)
    {
        foreach (var step in plan.Steps)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                step.Command,
                @"AZURE_LOCATION\s+([A-Za-z][A-Za-z0-9]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    private static RemediationDto ParseRemediation(string json, int failedStepId)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        var clean = (start >= 0 && end > start) ? json[start..(end + 1)] : "{}";

        using var doc = JsonDocument.Parse(clean);
        var r = doc.RootElement;

        var kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "give_up" : "give_up";
        var stepId = r.TryGetProperty("stepId", out var sid) ? ReadIntTolerant(sid, failedStepId) : failedStepId;
        var reasoning = r.TryGetProperty("reasoning", out var rs) ? rs.GetString() : null;

        var newSteps = new List<DeploymentStepDto>();
        if (r.TryGetProperty("newSteps", out var ns) && ns.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ns.EnumerateArray())
            {
                // Capture the optional typed-action subtree as a raw
                // JSON string. The host's ActionRegistry decides how
                // to dispatch it. The legacy "cmd" path still works
                // when "action" is absent.
                string? actionJson = null;
                if (s.TryGetProperty("action", out var actEl)
                    && actEl.ValueKind == JsonValueKind.Object)
                {
                    actionJson = actEl.GetRawText();
                }
                newSteps.Add(new DeploymentStepDto(
                    s.TryGetProperty("id", out var sIdEl) ? ReadIntTolerant(sIdEl, stepId) : stepId,
                    s.TryGetProperty("description", out var sDesc) ? sDesc.GetString() ?? "" : "",
                    s.TryGetProperty("cmd", out var sCmd) ? sCmd.GetString() ?? "" : "",
                    NormalizeCwd(s.TryGetProperty("cwd", out var sCwd) ? sCwd.GetString() : null))
                {
                    ActionJson = actionJson
                });
            }
        }

        return new RemediationDto(kind, stepId, newSteps, reasoning);
    }

    /// <summary>
    /// Reads an integer tolerantly from a <see cref="JsonElement"/>. The LLM
    /// occasionally returns decimals like 9.5 ('I want to insert BETWEEN
    /// step 9 and 10') or strings. Any non-integer value is coerced to the
    /// closest int, or the supplied fallback when coercion is impossible.
    /// Without this tolerance the runner used to crash with FormatException
    /// and throw away a perfectly valid remediation.
    /// </summary>
    private static int ReadIntTolerant(JsonElement el, int fallback)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                if (el.TryGetInt32(out var i)) return i;
                if (el.TryGetDouble(out var d)) return (int)Math.Round(d);
                return fallback;
            case JsonValueKind.String:
                var s = el.GetString();
                if (int.TryParse(s, out var si)) return si;
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var sd))
                    return (int)Math.Round(sd);
                return fallback;
            default:
                return fallback;
        }
    }

    /// <summary>
    /// Coerces an LLM-produced 'cwd' field into a value the host's
    /// PlanValidator will accept. The validator rejects rooted paths and
    /// any path containing '..'. The Strategist sometimes emits absolutes
    /// like '/workspace', '/workspace/api-app', '~/repo', or sprinkles
    /// '..' to climb directories. We strip the well-known sandbox prefix
    /// and fall back to '.' on anything else suspicious. Without this
    /// normalization the entire plan was rejected with
    /// "Working directory must be relative and inside workdir" on Step 1
    /// and the user lost the whole run.
    /// </summary>
    private static string NormalizeCwd(string? raw)
    {
        var v = (raw ?? ".").Trim();
        if (string.IsNullOrEmpty(v)) return ".";
        // Strip surrounding quotes if the LLM wrapped the path.
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
            v = v[1..^1].Trim();
        if (string.IsNullOrEmpty(v)) return ".";
        // Normalize backslashes the LLM may emit on Windows-style paths.
        v = v.Replace('\\', '/');
        // Drop trailing slash for a cleaner relative form.
        while (v.Length > 1 && v.EndsWith('/')) v = v[..^1];
        // Strip the well-known sandbox workdir prefixes.
        foreach (var prefix in new[] { "/workspace/", "/workdir/", "/repo/", "/app/", "/home/agent/workspace/" })
        {
            if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                v = v[prefix.Length..];
                break;
            }
        }
        // Bare workdir aliases collapse to '.'
        if (v is "/workspace" or "/workdir" or "/repo" or "/app" or "~" or "/")
            return ".";
        // Anything still rooted, climbing, or containing '..' is unsafe -> '.'
        if (v.Length == 0 || v.StartsWith('/') || v.StartsWith('~') || v.Contains(".."))
            return ".";
        return v;
    }


    private async Task<string> InvokeAsync(AIAgent agent, string input, CancellationToken ct)
    {
        // Stateless single-turn invocation: each agent in this sequential
        // pipeline gets its input, produces its output, and does not carry
        // conversation memory forward. We pass intermediate results
        // explicitly via the shared blackboard in RunAsync above.
        var response = await agent.RunAsync(input, cancellationToken: ct);
        return response.Text?.Trim() ?? "";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static string BuildClassifierInput(RepoInspector.ToolchainManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TOOLCHAIN MANIFEST:");
        sb.AppendLine(string.Join(", ", m.Summary()));
        sb.AppendLine();
        sb.AppendLine("DEPLOYABILITY SIGNALS:");
        sb.AppendLine($"  notebook count (.ipynb) : {m.NotebookCount}");
        sb.AppendLine($"  lesson/course folders   : {m.LessonFolderCount}");
        sb.AppendLine($"  has infrastructure-as-code : {m.HasInfrastructureAsCode}");
        sb.AppendLine($"  has deployment entrypoint  : {m.HasDeploymentEntry}");
        sb.AppendLine();
        sb.AppendLine("RATIONALE:");
        foreach (var r in m.Rationale) sb.AppendLine("- " + r);
        sb.AppendLine();
        sb.AppendLine("KEY FILES PRESENT:");
        foreach (var kv in m.KeyFileContents)
        {
            sb.AppendLine($"=== {kv.Key} ===");
            sb.AppendLine(kv.Value.Length > 1500 ? kv.Value[..1500] + "..." : kv.Value);
        }
        return sb.ToString();
    }

    private sealed record ClassificationVerdict(string? RepoKind, bool Deployable, string? Reason);

    /// <summary>
    /// Parses the TechClassifier JSON output just enough to decide whether
    /// the orchestrator should proceed to Strategist or short-circuit with
    /// a NotDeployable plan. Malformed / missing fields default to the
    /// permissive branch ('deployable=true') so legacy behaviour is
    /// preserved when the classifier is silent.
    /// </summary>
    private static ClassificationVerdict? ParseClassificationVerdict(string json)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            using var doc = JsonDocument.Parse(json[start..(end + 1)]);
            var r = doc.RootElement;
            var kind = r.TryGetProperty("repoKind", out var k) ? k.GetString() : null;
            // Treat missing / non-bool as 'deployable=true' to stay backward
            // compatible with older classifier outputs.
            var deployable = !r.TryGetProperty("deployable", out var d)
                || d.ValueKind != JsonValueKind.False;
            var reason = r.TryGetProperty("notDeployableReason", out var rr) ? rr.GetString() : null;
            return new ClassificationVerdict(kind, deployable, reason);
        }
        catch { return null; }
    }

    // ----------------------------------------------------------------
    // Baked helper table (sandbox image v17+).
    //
    // Tutti gli script `agentic-*` + `relocate-*` sono bakeati in
    // /usr/local/bin del sandbox image (vedi SandboxImageBuilder.cs).
    // Sono invocazioni a UN SOLO TOKEN, senza virgolette nidificate
    // né variabili interpolate dall'LLM. Questa tabella è iniettata
    // nel prompt dello Strategist E del Doctor: quando una scelta è
    // possibile, devono SEMPRE preferire l'helper alla shell raw.
    // ----------------------------------------------------------------
    private static void AppendBakedHelperTable(StringBuilder sb)
    {
        sb.AppendLine("BAKED HELPERS (sandbox image v32 — PREFER THESE OVER RAW SHELL):");
        sb.AppendLine("  These scripts live in /usr/local/bin and are invoked as ONE TOKEN.");
        sb.AppendLine("  No nested quotes, no $(...) interpolation needed. Idempotent.");
        sb.AppendLine();
        sb.AppendLine("  relocate-node-modules <root>");
        sb.AppendLine("      Symlinks every node_modules onto /tmp (NOEXEC bind-mount fix).");
        sb.AppendLine("  relocate-venv <root>");
        sb.AppendLine("      Same idea for Python .venv / venv folders.");
        sb.AppendLine("  agentic-azd-env-prime [<env-file>]");
        sb.AppendLine("      Forwards .env K=V lines into 'azd env set'.");
        sb.AppendLine("  agentic-azd-up [extra args...]");
        sb.AppendLine("      'azd up --no-prompt' with safe defaults + env priming.");
        sb.AppendLine("      Use INSTEAD of 'azd up --no-prompt' whenever possible.");
        sb.AppendLine("  agentic-azd-deploy [extra args...]");
        sb.AppendLine("      'azd deploy --no-prompt' with AZURE_RESOURCE_GROUP pre-resolved");
        sb.AppendLine("      (by tag) so it never fails with '0 resource groups'. Use as the");
        sb.AppendLine("      idempotent verification step after agentic-azd-up.");
        sb.AppendLine("  agentic-acr-build <ctx-dir> <dockerfile-rel> <image:tag>");
        sb.AppendLine("      Remote 'az acr build'. Auto-resolves registry from azd env.");
        sb.AppendLine("      Use when local docker build is risky (Angular/Vite/Next on arm64).");
        sb.AppendLine("  agentic-build <ctx> <dockerfile> <image:tag> [tmo-sec]");
        sb.AppendLine("      Local docker build with timeout + automatic ACR fallback.");
        sb.AppendLine("  agentic-npm-install <dir>");
        sb.AppendLine("      'npm ci' with 'npm install --no-audit --no-fund' fallback.");
        sb.AppendLine("  agentic-dotnet-restore <csproj-or-sln>");
        sb.AppendLine("      'dotnet restore' with 3-attempt retry.");
        sb.AppendLine("  agentic-bicep <bicep-file>");
        sb.AppendLine("      'az bicep build' (installs bicep CLI if missing).");
        sb.AppendLine("  agentic-clone <url> <dest>");
        sb.AppendLine("      'git clone --recursive --depth 1' with retry.");
        sb.AppendLine("  agentic-aca-wait <app-name> <rg> [tmo-sec]");
        sb.AppendLine("      Poll Container App provisioningState until terminal.");
        sb.AppendLine("  agentic-summary");
        sb.AppendLine("      Final deployment summary. MAKE THIS THE LAST STEP of every plan.");
        sb.AppendLine();
        sb.AppendLine("RULES FOR USING HELPERS:");
        sb.AppendLine("  - When a helper covers your need, EMIT THE HELPER, not raw shell.");
        sb.AppendLine("  - Never wrap a helper invocation in 'bash -lc \"...\"'.");
        sb.AppendLine("  - Helper invocations are ONE token + space-separated args. No quotes");
        sb.AppendLine("    around path arguments unless the path contains spaces.");
        sb.AppendLine("  - PROBE STEPS (read-only diagnostics like 'ls', 'cat', 'docker ps')");
        sb.AppendLine("    are PERMITTED but their description MUST start with '[Probe] ' so");
        sb.AppendLine("    the orchestrator does not consume the Doctor budget on them.");
        sb.AppendLine();
    }

    private static string BuildStrategistInput(string repoUrl,
        RepoInspector.ToolchainManifest m, string classification, string region,
        IReadOnlyList<PriorInsightDto>? priorInsights)
    {
        var sb = new StringBuilder();
        sb.Append("REPO: ").AppendLine(repoUrl);
        sb.Append("TARGET REGION: ").AppendLine(region);
        sb.AppendLine("CLASSIFICATION:");
        sb.AppendLine(classification);
        sb.AppendLine();
        sb.AppendLine("DETECTED TOOLCHAINS:");
        sb.AppendLine(string.Join(", ", m.Summary()));
        sb.AppendLine();

        // Inspector ground-truth on azure.yaml. Banner-style so the
        // Strategist cannot miss it: this is the difference between
        // "use the azd flow" and "you MUST use Strategy 1b". We put it
        // BEFORE rationale because LLMs weight banners higher than
        // bullet lists buried in long context.
        if (!m.Azd)
        {
            sb.AppendLine("================================================================");
            sb.AppendLine("HARD CONSTRAINT - NO 'azure.yaml' AT THE REPO ROOT.");
            sb.AppendLine("================================================================");
            sb.AppendLine("- DO NOT emit 'azd up', 'azd provision', 'azd deploy', 'azd env new'.");
            sb.AppendLine("  These commands REQUIRE an existing azure.yaml and will fail with");
            sb.AppendLine("  'no project exists'.");
            sb.AppendLine("- DO NOT scaffold a new azure.yaml ('azd init --template-empty',");
            sb.AppendLine("  'azd init --from-code', hand-written azure.yaml).");
            sb.AppendLine("- USE Strategy 1b (Bicep-direct):");
            sb.AppendLine("    az group create -n rg-<env> -l <region>");
            sb.AppendLine("    az deployment group create -g rg-<env> -f <main.bicep>");
            sb.AppendLine("        [--parameters @<params.json>  ONLY IF that file actually exists in the repo]");
            sb.AppendLine("        [--parameters key=value ...    when bicep params have no defaults]");
            sb.AppendLine("    agentic-acr-build <ctx> <Dockerfile> <imageName>     # one per Dockerfile");
            sb.AppendLine("    az containerapp update --image <acr>.azurecr.io/<img>:latest ...");
            sb.AppendLine("- PARAMETERS FILE RULE (CRITICAL): NEVER pass '--parameters @<file>' unless");
            sb.AppendLine("  the FILE LOCATIONS section below explicitly lists that file. If no");
            sb.AppendLine("  '*.parameters.json' is listed, OMIT the '@' form entirely. The Bicep");
            sb.AppendLine("  template usually has parameter defaults; pass overrides as '--parameters");
            sb.AppendLine("  key=value' only for required-without-default params. Hallucinating a");
            sb.AppendLine("  parameters path that does not exist on disk fails with");
            sb.AppendLine("  'Unable to parse parameter: @<path>' and burns a Doctor attempt.");
            sb.AppendLine("- IF THE REPO SHIPS DEPLOY SCRIPTS (e.g. infra/*.sh, deploy.sh, scripts/deploy*.sh,");
            sb.AppendLine("  Makefile target named 'deploy'), STRONGLY PREFER reproducing them as 'bash <script>'");
            sb.AppendLine("  in numerical / lexical order � the authors already encoded the right CLI");
            sb.AppendLine("  invocations, parameter values, and step ordering. Hand-rolling the steps");
            sb.AppendLine("  from the bicep alone duplicates work and tends to invent parameter files.");
            sb.AppendLine("- If a deploy.sh / Makefile target / npm-script ships in the repo, prefer");
            sb.AppendLine("  reproducing it verbatim.");
            sb.AppendLine("- ONLY if NEITHER infra/*.bicep, *.tf, Dockerfile NOR a documented deploy");
            sb.AppendLine("  command exists, emit a 1-step plan with 'agentic-summary' and a");
            sb.AppendLine("  description prefixed '[Escalate] repository ships no deployment artifacts'.");
            sb.AppendLine("================================================================");
            sb.AppendLine();
        }

        if (m.Rationale.Count > 0)
        {
            sb.AppendLine("FILE LOCATIONS (use these exact paths, do not guess):");
            foreach (var r in m.Rationale) sb.AppendLine("  " + r);
            sb.AppendLine();
        }

        // Surface the actual infra/ tree so the Strategist can SEE that
        // (for example) main.parameters.json does NOT exist before it
        // hallucinates '--parameters @infra/main.parameters.json'. Also
        // lets Strategy 1b prefer ready-made deploy scripts when the
        // repo ships them.
        if (m.InfraFiles.Count > 0)
        {
            sb.AppendLine("INFRA TREE (verbatim - do NOT reference paths NOT in this list):");
            foreach (var f in m.InfraFiles.Take(40)) sb.AppendLine("  " + f);
            if (m.InfraFiles.Count > 40) sb.AppendLine($"  ... and {m.InfraFiles.Count - 40} more");
            sb.AppendLine();
        }

        AppendPriorInsights(sb, priorInsights);

        // Preventive injection (Node services on Windows / Docker Desktop):
        // /workspace inside the sandbox is a bind-mount of a Windows path.
        // npm extracts node_modules/<pkg>/bin/<bin> with the executable
        // bit set in the tarball, but the WSL2 -> Windows mount driver
        // strips it on the way through, so the next 'spawnSync' fails
        // EACCES. The canonical fix is to relocate node_modules into
        // /tmp (Linux-native, exec-capable) via symlink BEFORE 'azd up'
        // runs the npm hooks. Without this preventive step every Node-
        // heavy template (Angular, Vite, Next, Modern.js) burns the
        // Doctor budget on micro-variations of '--ignore-scripts' that
        // only mask the install failure, not the runtime build failure.
        if (m.Node)
        {
            sb.AppendLine("NODE.JS PREVENTIVE STEP (PUT IT IN THE INITIAL PLAN, BEFORE 'azd up'):");
            sb.AppendLine("  The repo contains package.json. The /workspace bind-mount on");
            sb.AppendLine("  Windows + Docker Desktop is NOEXEC for files extracted by npm");
            sb.AppendLine("  (esbuild, rollup, vite, ng, next... all fail with EACCES).");
            sb.AppendLine("  The sandbox image ships a baked-in helper that relocates");
            sb.AppendLine("  node_modules of every package.json subfolder onto /tmp via");
            sb.AppendLine("  symlink. Add EXACTLY ONE step BEFORE 'azd up':");
            sb.AppendLine();
            sb.AppendLine("    relocate-node-modules /workspace");
            sb.AppendLine();
            sb.AppendLine("  No bash wrapping, no quoting, no variables — emit the command");
            sb.AppendLine("  exactly as shown. The helper is idempotent and safe to call");
            sb.AppendLine("  even when no package.json is present.");
            sb.AppendLine();
            sb.AppendLine("  This is INVISIBLE to npm/azd (they see node_modules where they");
            sb.AppendLine("  expect it) but the actual files live on /tmp where the exec bit");
            sb.AppendLine("  IS preserved. Only ONE such step is needed; it covers every");
            sb.AppendLine("  package.json in the repo automatically.");
            sb.AppendLine();
        }
        if (m.Python)
        {
            sb.AppendLine("PYTHON PREVENTIVE STEP (PUT IT IN THE INITIAL PLAN, BEFORE 'azd up'):");
            sb.AppendLine("  The repo contains requirements.txt or pyproject.toml. Same NOEXEC");
            sb.AppendLine("  story as Node: pip extracts wheels with +x, the bind-mount strips");
            sb.AppendLine("  them, the next 'python -m <module>' fails. Add EXACTLY ONE step:");
            sb.AppendLine();
            sb.AppendLine("    relocate-venv /workspace");
            sb.AppendLine();
            sb.AppendLine("  Idempotent; safe even when no .venv yet exists.");
            sb.AppendLine();
        }
        // Always advertise the full agentic-* helper table. The Strategist
        // is REQUIRED to prefer these single-token helpers over hand-rolled
        // shell. The Doctor reads the same prompt context; its remediation
        // patches will also be helper invocations.
        AppendBakedHelperTable(sb);
        // Preventive injection: if global.json pins a .NET SDK major that
        // the sandbox image doesn't carry, tell the Strategist upfront so
        // the "relax the pin" step is part of the initial plan � not a
        // late-bound Doctor remediation after 3+ failed 'azd package'
        // invocations. This alone eliminates an entire class of 10+
        // attempt loops we've observed on arm64 Docker Desktop.
        if (!m.DotnetSdkPinSatisfiable && m.DotnetSdkPin is not null)
        {
            sb.AppendLine("DOTNET SDK PIN (PREVENTIVE � include a fix step IN THE INITIAL PLAN):");
            sb.AppendLine($"  The repo's global.json pins SDK '{m.DotnetSdkPin}' (major " +
                          $"{m.DotnetSdkPinMajor}), but the sandbox image provides SDK 8 and 9 only.");
            sb.AppendLine("  BEFORE the 'azd up' step, emit ONE step with:");
            sb.AppendLine("    bash -lc \"sed -i 's/\\\"version\\\": \\\"[0-9.]*\\\"/\\\"version\\\": \\\"8.0.100\\\"/' /workspace/global.json\"");
            sb.AppendLine("  Without this, 'azd package' will call 'dotnet user-secrets' " +
                          "which hits 'No .NET SDKs were found' and burns Doctor attempts.");
            sb.AppendLine();
        }

        if (m.AzdRequiredEnvVars.Count > 0)
        {
            sb.AppendLine("AZD REQUIRED ENV VARS (extracted from infra/main.parameters.json):");
            sb.AppendLine("You MUST emit one 'azd env set <VAR> <value>' step for EACH of these");
            sb.AppendLine("before 'azd up --no-prompt', otherwise provisioning fails with");
            sb.AppendLine("'missing required inputs'. For any var whose name contains LOCATION or");
            sb.AppendLine("REGION, reuse the same value you set for AZURE_LOCATION.");
            foreach (var v in m.AzdRequiredEnvVars) sb.AppendLine("  - " + v);
            sb.AppendLine();
        }
        if (m.HookCommands.Count > 0)
        {
            sb.AppendLine("AZURE.YAML HOOK COMMANDS (azd runs these AUTOMATICALLY " +
                          "during 'azd up' � DO NOT add them as manual steps):");
            foreach (var h in m.HookCommands.Take(6))
            {
                sb.AppendLine("---");
                sb.AppendLine(h.Length > 500 ? h[..500] + "..." : h);
            }
            sb.AppendLine("---");
        }
        sb.AppendLine();
        if (!string.IsNullOrEmpty(m.Readme))
        {
            sb.AppendLine("README (truncated):");
            sb.AppendLine(m.Readme);
        }
        return sb.ToString();
    }

    private const string StrategistInstructions = """
        You are a senior deployment engineer. Produce ONLY a JSON deployment
        plan matching this schema:
        {
          "prerequisites": [string],
          "env": { "KEY": "VALUE" },
          "steps": [ { "id": int, "description": string, "cmd": string, "cwd": string } ],
          "verifyHints": [string]
        }

        EXECUTION CONTEXT
        - The repository is ALREADY cloned at the working directory.
        - DO NOT generate 'git clone' or 'azd init -t ...' � the code is present.
        - The sandbox has its OWN 'az login' cached in a persistent Docker
          volume. azd discovers the current user, tenant and subscription
          via 'az account show'. Do NOT assume AZURE_SUBSCRIPTION_ID /
          AZURE_TENANT_ID are pre-set and do NOT emit 'az login' / 'azd auth
          login' steps.
        - The sandbox has: az, azd, docker, git, node+npm, python+pip+uv,
          tar, zip, unzip, jq. Do NOT reinstall these.

        AZD HOOKS ARE AUTOMATIC � DO NOT DUPLICATE THEM
        - If 'azure.yaml' declares hooks (prebuild / prepackage / predeploy /
          postdeploy / preprovision / postprovision), 'azd up' invokes them
          automatically at the right phase.
        - DO NOT add manual steps like 'cd app/frontend && npm install &&
          npm run build' if those commands already appear inside a hook in
          azure.yaml. You would run them twice, with wrong cwd, and break
          the deploy. Let azd do its job.
        - For a canonical azd repo the base plan is:
            1) azd env new <unique-name> --no-prompt
            2) azd env set AZURE_SUBSCRIPTION_ID "$(az account show --query id -o tsv)"
            3) azd env set AZURE_TENANT_ID "$(az account show --query tenantId -o tsv)"
            4) azd env set AZURE_LOCATION <TARGET REGION from the input header>
            5+) one 'azd env set' per required azd env var (see below)
            PENULTIMATE) azd up --no-prompt
            LAST)        agentic-azd-deploy   (ONLY when the repo
                         targets Azure Container Apps � see rule below)

        AZURE CONTAINER APPS: ALWAYS ADD A 'azd deploy' VERIFICATION STEP
        - 'azd up' runs THREE phases: package (build container images) +
          provision (create Azure resources) + deploy (push images + update
          Container App revisions). In practice, when the repo targets
          Azure Container Apps, the deploy phase can silently fail or be
          skipped while provision still reports success � leaving every
          Container App stuck on the Microsoft placeholder image
          'mcr.microsoft.com/azuredocs/containerapps-helloworld'. The user
          sees "azd up finished" and an app that just says "Hello World".
        - DETECTION HEURISTIC FOR CONTAINER APPS: the repo targets
          Container Apps when ANY of the following holds:
            � azure.yaml declares 'host: containerapp' on any service;
            � infra/*.bicep references 'Microsoft.App/containerApps'
              or 'Microsoft.App/managedEnvironments';
            � at least one Dockerfile is listed in the Scout's rationale
              AND azure.yaml has a 'services:' section.
          If you cannot tell, assume it DOES (an extra `azd deploy` is
          idempotent and costs ~1 minute on a healthy run; a missing one
          costs an entire re-deploy).
        - WHEN DETECTED, append EXACTLY ONE extra step AFTER 'azd up':
            { "description": "Ensure container images are built and pushed
              (azd deploy � idempotent verification after azd up)",
              "cmd": "agentic-azd-deploy", "cwd": "." }
          This is NOT a retry: it is a deterministic safety net that
          either runs in ~5 seconds ("nothing to deploy") or actually
          recovers a hollow deploy.
        - NEVER replace 'azd up' with 'azd provision' + 'azd deploy' as
          two separate steps just to sidestep build errors. Keep 'azd up'
          as the primary entry; add 'azd deploy' only as the trailing
          verification step described above.

        USE THE PROVIDED TARGET REGION
        - The request header contains 'TARGET REGION: <region>'. Use THAT
          exact value for AZURE_LOCATION and for any *_LOCATION / *_REGION
          variables surfaced by the Scout. Never hard-code 'eastus' unless
          the header literally says 'eastus'.

        PIN THE SUBSCRIPTION AND TENANT EXPLICITLY
        - Steps 2 and 3 above are MANDATORY right after 'azd env new'.
        - They query the sandbox's own 'az login' for the current user's
          default subscription and tenant, so azd never tries to prompt
          with "Select an Azure Subscription to use" (which fails under
          --no-prompt when the user has more than one subscription).
        - Always use the exact syntax with $(...) command substitution
          inside double quotes. NEVER hard-code a subscription GUID.

        AZD REQUIRED ENV VARS � ALWAYS SET THEM BEFORE 'azd up'
        - When the Scout reports azd-required env vars (extracted from
          infra/main.parameters.json), you MUST add ONE 'azd env set ...'
          step per variable, placed BEFORE the final 'azd up' step.
        - For *LOCATION / *REGION vars, reuse the same region you set for
          AZURE_LOCATION (safe default; the constraint lists in azd error
          messages are usually a superset that includes eastus).
        - For other vars, pick a sensible default or empty string; azd will
          validate and fail fast if the value is wrong, and the user can
          amend the plan via the approval step.
        - Emit ONE step per env var. DO NOT collapse multiple env vars into a
          single shell command with a 'for' loop, 'xargs', or chained '&&'.
          Each step must be independently readable, diffable, and revertible.
          The sandbox already isolates the plan; readability matters more
          than shell cleverness.

        PATHS MUST MATCH THE SCOUT � NEVER GUESS
        - The Scout reports the EXACT relative path of every marker file in
          its rationale (e.g. 'package.json -> app/frontend/package.json').
        - If you must reference a subfolder, use that exact path. If the
          scout says 'app/frontend', the cwd is 'app/frontend', NOT 'frontend'.
        - If the relevant file lives in a subfolder, put that subfolder in
          the step's 'cwd' field instead of using 'cd' inside 'cmd'.

        CWD FORMAT (HARD RULE)
        - 'cwd' MUST be a RELATIVE path inside the cloned repo. Allowed:
          '.', 'api-app', 'app/frontend', 'infra'. FORBIDDEN: '/workspace',
          '/workspace/api-app', '/repo', '~', '..', '../api-app', or any
          path that starts with '/' or '~' or contains '..'. The repo is
          mounted at the working directory; the runner runs each step
          there with no other context. Using an absolute path causes the
          host validator to REJECT THE ENTIRE PLAN with
          "Working directory must be relative and inside workdir."

        STRATEGY
        1. If 'azure.yaml' exists with services + Bicep/Terraform under
           infra/, use the three-step azd flow above. Ignore README prose
           about manual build steps � azd hooks cover them.
        1b. NO 'azure.yaml' AT THE REPO ROOT -> NEVER EMIT azd up/provision/
            deploy/env new. `azd env new` requires a project (azure.yaml)
            and will fail with "no project exists". DO NOT invent
            scaffolding commands like `azd init --template-empty`,
            `azd init --from-code`, or hand-written `azure.yaml`. Pick a
            different strategy that uses ONLY the deployment artifacts the
            authors actually shipped:
              � Bicep present (root or infra/) + Dockerfile(s) ->
                Bicep-direct flow:
                  az group create -n rg-<env> -l <region>
                  az deployment group create -g rg-<env> -f <main.bicep> --parameters @<params> [--parameters key=value]
                  for each Dockerfile that should ship as an image:
                    agentic-acr-build <ctx> <Dockerfile> <imageName>
                  az containerapp update / az webapp config container set as needed
              � Terraform only -> terraform init / plan / apply
              � docker-compose only and the README documents `docker
                compose up` as the deploy path -> reproduce that.
              � README documents an explicit deploy command (npm run
                deploy, make deploy, ./deploy.sh) -> reproduce it.
            If NONE of the above applies (e.g. the README only documents
            `python main.py` / `npm run dev` for local development and
            there is no IaC), the repository is NOT deployable as-is.
            In that case the TechClassifier should have already flagged
            it; if you are running anyway, emit a 1-step plan whose only
            step is `agentic-summary` with description prefixed
            '[Escalate] repository ships no deployment artifacts' so
            the orchestrator surfaces a clear message instead of running
            a hallucinated deploy.
        2. Else, if README has a 'Deployment' / 'Getting Started' /
           'Quickstart' section, reproduce those commands (skip clone/init).
        3. Else, infer from the classification and manifest: docker compose
           up, terraform apply, npm run deploy, make deploy, etc.
        4. Use non-interactive flags everywhere (--no-prompt, --yes,
           --only-show-errors, --use-device-code if login is truly required).
        5. Commands must start with one of: git, azd, az, pac, docker,
           dotnet, npm, node, python, pip, bash, sh, make, terraform,
           or one of the BAKED HELPERS listed in the input header (those
           are ALWAYS preferred when applicable).
        5b. PREFER BAKED HELPERS OVER HAND-ROLLED SHELL.
            The input header lists every 'agentic-*' / 'relocate-*' helper
            available in /usr/local/bin. Each is a single-token command
            with no nested quoting. When a helper covers your need (NOEXEC
            relocation, npm install, dotnet restore, ACR build, ...), emit
            the HELPER. Do NOT emit equivalent multi-line bash. Reasoning:
            you cannot reliably emit nested-quoted shell, and the orchestrator
            verifier rejects 'bash -lc "..."' with multi-level escaping.
        5c. PROBE STEPS DO NOT CONSUME DOCTOR BUDGET.
            Read-only diagnostic steps (ls, cat, docker ps, az resource list,
            azd env get-values, ...) are USEFUL when a step has just failed
            and you need to inspect state, but ONLY if you tag them. To tag
            a step, prefix its 'description' with the literal '[Probe] '.
            The orchestrator will execute the step, surface its output, and
            NOT count its failure (if any) against the Doctor's per-session
            attempt budget. Untagged read-only steps DO consume budget.
        5d. END EVERY PLAN WITH 'agentic-summary'.
            The final step of every plan must have cmd='agentic-summary',
            cwd='.', description='[Probe] Final deployment summary'. This
            step is best-effort and never fails the deploy; it gives the
            UI something useful to render even when an upstream step
            failed-but-recovered.

        SANDBOX SHELL � READ CAREFULLY
        The sandbox image is a minimal Debian with ONLY these shells and
        tools installed: bash, sh, az, azd, docker, dotnet (SDK + runtime),
        git, npm, node, python3, pip, make, terraform, sed, find, chmod,
        dos2unix, tr. POWERSHELL IS NOT INSTALLED. Consequences:
          � Do NOT emit 'pwsh -c "..."', 'powershell -Command "..."', or
            any PowerShell-only construct. These fail with exit 127
            'pwsh: command not found' and waste a Doctor attempt.
          � Command substitution: use bash syntax '$(...)' wrapped in
            'bash -lc "..."', e.g.:
              bash -lc "azd env set AZURE_SUBSCRIPTION_ID $(az account show --query id -o tsv)"
            NEVER use PowerShell pipeline syntax like
            '$(az ... | ConvertFrom-Json).id'.
          � Variables: prefer 'azd env set VAR value' for fixed values, and
            'bash -lc "azd env set VAR $(...)"' for dynamic ones.
          � If a value is already known at plan time (e.g. the target
            region is fixed), just inline the literal string � no shell
            substitution at all is simplest and most robust.
        6. azd environment names must be unique (e.g. '<repo>-agentichub').
        7. Keep the plan under 10 steps (the Container Apps verification
           step counts towards this � budget for it).
        8. Set verifyHints[0] = "source: README section '<heading>'" when
           Strategy 2 was used, otherwise "source: inferred from <files>".

        Respond with ONLY the JSON object, no prose.
        """;

    private static DeploymentPlanDto ParsePlan(string json, RepoInspector.ToolchainManifest manifest)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        var clean = (start >= 0 && end > start) ? json[start..(end + 1)] : "{}";

        using var doc = JsonDocument.Parse(clean);
        var r = doc.RootElement;

        var steps = new List<DeploymentStepDto>();
        if (r.TryGetProperty("steps", out var stepsEl))
        {
            foreach (var s in stepsEl.EnumerateArray())
            {
                string? actionJson = null;
                if (s.TryGetProperty("action", out var actEl)
                    && actEl.ValueKind == JsonValueKind.Object)
                {
                    actionJson = actEl.GetRawText();
                }
                steps.Add(new DeploymentStepDto(
                    s.TryGetProperty("id", out var id) ? ReadIntTolerant(id, steps.Count + 1) : steps.Count + 1,
                    s.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    s.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "",
                    NormalizeCwd(s.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null))
                {
                    ActionJson = actionJson
                });
            }
        }

        var env = new Dictionary<string, string>();
        if (r.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                env[p.Name] = p.Value.GetString() ?? "";

        var prereq = r.TryGetProperty("prerequisites", out var p2) && p2.ValueKind == JsonValueKind.Array
            ? p2.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();

        var hints = r.TryGetProperty("verifyHints", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();

        return new DeploymentPlanDto(prereq, env, steps, hints)
        {
            // Deployable path emits a real plan; keep IsDeployable=true and
            // surface the repoKind so the UI can still tag it (e.g. 'app',
            // 'monorepo') for the history card.
            IsDeployable = true,
            RepoKind = "app"
        };
    }
}
