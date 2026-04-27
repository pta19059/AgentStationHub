using System.ClientModel;
using System.Text.Json;
using AgentStationHub.Models;
using AgentStationHub.Services.Tools;
using OpenAI.Responses;

namespace AgentStationHub.Services.Agents;

#pragma warning disable OPENAI001
public sealed class PlanExtractorAgent
{
    private readonly OpenAIResponseClient _responses;

    public PlanExtractorAgent(OpenAIResponseClient responses) => _responses = responses;

    public async Task<DeploymentPlan> ExtractAsync(
        string repoUrl,
        string readme,
        IEnumerable<string> infraFiles,
        IReadOnlyDictionary<string, string> keyFileContents,
        RepoInspector.ToolchainManifest toolchain,
        CancellationToken ct)
    {
        const string system = """
            You are a senior deployment engineer. You receive a repository's README,
            a list of files under infra/, the CONTENT of key orchestration files
            (azure.yaml, Dockerfile, docker-compose.yml, package.json, pyproject.toml,
            requirements.txt, Makefile, setup.sh, infra/main.bicep, infra/main.tf),
            a pre-computed TOOLCHAIN manifest (what the repo needs: Node, Python,
            .NET, etc.) and � when present � the exact AZURE.YAML HOOK COMMANDS
            that azd will run during 'package' and 'deploy'. Use all of this to
            avoid guessing.

            IMPORTANT EXECUTION CONTEXT
            - The repository is ALREADY cloned into the working directory of every
              step. Treat '.' as the repo root: all commands run from there unless
              they explicitly cd into a subfolder.
            - DO NOT generate 'git clone', 'azd init -t ...', 'azd template',
              'npm create', 'npx create-*' or any other bootstrapper that would
              download or regenerate the project. The files are already present.
            - The sandbox is already logged in to Azure via its own 'az login'
              in a persistent Docker volume. azd discovers the current user
              and subscription via 'az account show'. Do NOT emit 'azd auth
              login' or 'az login' unless the sandbox auth volume is known to
              be missing.
            - The sandbox already contains: az, azd, docker CLI, git, tar, zip,
              unzip, jq, node+npm, python3+pip+uv. Bicep is auto-downloaded by
              azd on demand. Do NOT add steps that reinstall these tools.

            Your task: build a MINIMAL, EXECUTABLE deployment plan.

            STRATEGY (apply in this order, stop at the first match):

            ---- STRATEGY A: extract steps from the README ----
            Carefully scan the README for a section that documents the deployment
            or run flow. Common headings include:
              "Deployment", "Deploy", "Getting Started", "Quickstart",
              "Installation", "Setup", "Running locally", "Running the app",
              "How to run", "How to deploy", "Deploy to Azure", "Run it yourself".
            If you find such a section, REPRODUCE the documented command sequence
            as-is, SKIPPING any 'git clone', 'azd init -t', or bootstrap command
            whose purpose is to fetch the repo: we already have it.
            Prefer the commands inside fenced code blocks (```bash, ```sh,
            ```shell, ```powershell) in that order of appearance.
            - Translate interactive prompts to non-interactive flags where possible
              (e.g. add '--no-prompt', '--yes', '--use-device-code').
            - Keep the SAME working directory and environment variables the README
              mentions.
            - Set verifyHints[0] = "source: README section '<heading>'".

            ---- STRATEGY B: infer from repository files ----
            Only if the README does NOT document an explicit deployment/run flow,
            infer the plan from the key files, applying these rules in order:
             1. Valid 'azure.yaml' with services + Bicep/Terraform under infra/ ->
                azd env new <unique-env> --no-prompt,
                azd env set AZURE_SUBSCRIPTION_ID "$(az account show --query id -o tsv)",
                azd env set AZURE_TENANT_ID "$(az account show --query tenantId -o tsv)",
                azd env set AZURE_LOCATION <region>,
                azd up --no-prompt.
             2. docker-compose.yml present -> 'docker compose up -d --build'.
             3. Dockerfile present -> 'docker build' + 'docker run' or
                'az containerapp up' with explicit flags.
             4. infra/main.bicep -> 'az deployment group create ...'.
             5. infra/main.tf    -> 'terraform init && terraform apply -auto-approve'.
             6. package.json with 'deploy' script -> 'npm ci && npm run deploy'.
             7. Makefile with a 'deploy' target -> 'make deploy'.
             8. Otherwise emit a diagnostic plan that exits 1 and explain in
                verifyHints what is missing.
            - Set verifyHints[0] = "source: inferred from <file1>, <file2>".

            PIN SUBSCRIPTION + TENANT FROM THE SANDBOX LOGIN
            - For any azd-based plan you MUST insert these two steps right
              after 'azd env new' and BEFORE any other 'azd env set':
                azd env set AZURE_SUBSCRIPTION_ID "$(az account show --query id -o tsv)"
                azd env set AZURE_TENANT_ID "$(az account show --query tenantId -o tsv)"
            - This binds azd to whichever subscription the sandbox user has
              selected as default (via 'az login'). Without these steps azd
              fails under --no-prompt with 'reading subscription id: no
              default response for prompt'.

            OUTPUT
            Respond ONLY with a JSON object (no prose, no markdown):
            {
              "prerequisites": [string],
              "env": { "KEY": "VALUE" },
              "steps": [
                { "id": int, "description": string, "cmd": string, "cwd": string }
              ],
              "verifyHints": [string]
            }

            HARD CONSTRAINTS
            - Commands MUST start with one of: git, azd, az, pac, docker, dotnet,
              npm, node, pwsh, python, pip, bash, sh, make, terraform.
            - Use non-interactive flags: 'azd up --no-prompt', 'az ... --yes',
              '--only-show-errors'.
            - AVOID 'azd auth login' / 'az login' in automated flows; assume the
              host already has valid credentials. If login is strictly required,
              use '--use-device-code' so the device code appears in the log.
            - When creating an azd environment, NEVER use a generic name like
              'demo', 'test', 'env'. Use something unique such as
              '<repo-name>-agentichub'.
            - cwd is always relative to the repo root (default ".").
            - Never emit 'rm -rf /', 'curl | sh', 'Invoke-Expression', or anything
              that downloads and executes a remote script.
            - Each step's 'cmd' is a single shell command (pipes and && allowed).
            - Keep the plan under 8 steps.
            """;

        var sb = new System.Text.StringBuilder();
        sb.Append("REPO: ").AppendLine(repoUrl);
        sb.AppendLine();
        sb.AppendLine("DETECTED TOOLCHAINS (from static inspection):");
        var summary = string.Join(", ", toolchain.Summary());
        sb.AppendLine(string.IsNullOrEmpty(summary) ? "  (none)" : "  " + summary);
        if (toolchain.HookCommands.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("AZURE.YAML HOOK COMMANDS (real prerequisites at deploy time):");
            foreach (var h in toolchain.HookCommands.Take(6))
            {
                var trimmed = h.Length > 600 ? h[..600] + "..." : h;
                sb.AppendLine("---");
                sb.AppendLine(trimmed);
            }
            sb.AppendLine("---");
        }
        sb.AppendLine();
        sb.AppendLine("README (truncated):");
        sb.AppendLine(readme);
        sb.AppendLine();
        sb.AppendLine("INFRA FILES:");
        sb.AppendLine(string.Join('\n', infraFiles));
        sb.AppendLine();
        sb.AppendLine("KEY FILE CONTENTS (only those that exist in the repo):");
        if (keyFileContents.Count == 0)
        {
            sb.AppendLine("(none of the key files are present in this repository)");
        }
        else
        {
            foreach (var kv in keyFileContents)
            {
                sb.Append("--- ").Append(kv.Key).AppendLine(" ---");
                sb.AppendLine(kv.Value);
                sb.AppendLine();
            }
        }

        var items = new List<ResponseItem>
        {
            ResponseItem.CreateSystemMessageItem(system),
            ResponseItem.CreateUserMessageItem(sb.ToString())
        };

        var options = new ResponseCreationOptions
        {
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonObjectFormat()
            }
        };

        ClientResult<OpenAIResponse> result = await _responses.CreateResponseAsync(items, options, ct);
        var content = result.Value.GetOutputText() ?? "{}";
        var json = ExtractJson(content);

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var steps = new List<DeploymentStep>();
        if (r.TryGetProperty("steps", out var stepsEl))
        {
            foreach (var s in stepsEl.EnumerateArray())
            {
                // Optional typed action: when "action" is an object,
                // serialize it back to JSON so the orchestrator's
                // ActionRegistry can parse it. The legacy "cmd" path
                // remains as fallback for steps the model authored
                // as bash strings.
                string? actionJson = null;
                if (s.TryGetProperty("action", out var actEl)
                    && actEl.ValueKind == JsonValueKind.Object)
                {
                    actionJson = actEl.GetRawText();
                }
                steps.Add(new DeploymentStep(
                    Id: s.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : steps.Count + 1,
                    Description: s.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Command: s.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "",
                    WorkingDirectory: s.TryGetProperty("cwd", out var cwd) ? cwd.GetString() ?? "." : ".")
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

        return new DeploymentPlan(repoUrl, prereq, env, steps, hints);
    }

    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return (start >= 0 && end > start) ? s[start..(end + 1)] : "{}";
    }
}
#pragma warning restore OPENAI001
