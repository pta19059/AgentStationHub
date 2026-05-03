using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Ensures a working sandbox Docker image exists for the current host architecture.
/// On arm64 hosts (e.g. Windows-on-ARM, Apple Silicon) the default
/// 'mcr.microsoft.com/azure-dev-cli-apps' image is amd64-only and crashes under
/// QEMU emulation (SIGSEGV in the Go runtime during DNS operations), so we build
/// a small multi-arch replacement once and reuse it for every step.
/// </summary>
public static class SandboxImageBuilder
{
    // Version bump:
    //   v7 -> v8: added .NET SDK 8+9 (was runtime-only).
    //   v8 -> v9: added the docker CLI client (static binary). The sandbox
    //             now participates in Docker-out-of-Docker: it receives
    //             /var/run/docker.sock bind-mounted at container start
    //             and uses the host daemon for any 'docker build' that
    //             azd invokes (Container Apps with local image build,
    //             AKS deploys, ...). Without this, repos with an
    //             azure.yaml 'docker:' section trigger 'neither docker
    //             nor podman is installed' and the Doctor cannot recover
    //             (stubs are rejected by azd's LookPath-based runtime
    //             check).
    //   v9 -> v10: added BuildKit support (DOCKER_BUILDKIT=1) + the
    //             buildx plugin. Modern Dockerfiles from azd templates
    //             frequently use '--mount=type=cache' / '--mount=type=
    //             secret' / '--mount=type=bind' RUN flags, all of which
    //             require BuildKit. Without these the Doctor gets stuck
    //             in a loop attempting sed-based rewrites that strip the
    //             cache-mount lines but can't repair the resulting
    //             Dockerfile semantics. Enabling BuildKit delegates the
    //             parsing to the host daemon (Docker Desktop has it
    //             on by default) and the loop disappears.
    //   v10 -> v11: pre-compile Python bytecode for the azure-cli tree.
    //             Cold-start of 'az' was 10-15 s inside the sandbox
    //             (Python interpreter boot + compiling .pyc on the fly
    //             for every imported module) which exceeded Go's 10 s
    //             AzureCLICredential timeout in azd, producing the
    //             infamous 'AzureCLICredential: signal: killed' error
    //             at the very start of 'azd up'. A single 'compileall'
    //             pass at image build time caches every .pyc so cold
    //             starts drop to ~4 s and azd's credential chain passes.
    //   v11 -> v12: install the docker compose v2 plugin AND a
    //             /usr/local/bin/docker-compose shim that forwards to
    //             'docker compose'. Many sample repos (notably the
    //             azure-ai-travel-agents postprovision MCP setup) call
    //             the legacy hyphenated 'docker-compose -f file.yml ...'
    //             which used to fail with 'command not found' (the
    //             shell's word-splitting then made `-f` look like a
    //             docker flag, dumping `docker --help` and aborting
    //             azd up). With both forms available out of the box
    //             every variant of compose invocation a hook script
    //             might use just works.
    //   v15 -> v16: bake `/usr/local/bin/relocate-node-modules` into
    //             the image so the Strategist's NOEXEC-bind-mount
    //             preventive step is a SINGLE COMMAND with no nested
    //             quotes ('relocate-node-modules /workspace'). The
    //             previous design embedded a multi-line bash template
    //             with `mapfile`, `${var:-default}` and nested `\"`
    //             into the LLM prompt; the LLM repeatedly dropped one
    //             level of escaping so when the orchestrator wrapped
    //             the result in `bash -lc "..."`, the outer shell
    //             closed the string mid-script and `${pjs[@]}` /
    //             `${slug:-root}` were evaluated in the wrong scope,
    //             producing `mkdir: cannot create directory ''`. The
    //             baked script is identical to the C# last-resort
    //             relocation logic in DeploymentOrchestrator; a single
    //             source of truth, no LLM-quoting risk.
    //   v16 -> v17: bake the FULL `agentic-*` helper toolbox (10 more
    //             scripts on top of relocate-node-modules):
    //               agentic-help, agentic-summary, agentic-azd-up,
    //               agentic-azd-env-prime, agentic-acr-build,
    //               agentic-build, agentic-npm-install,
    //               agentic-dotnet-restore, agentic-bicep,
    //               agentic-clone, agentic-aca-wait, relocate-venv.
    //             Rationale: the Strategist prompt now exposes a
    //             FINITE table of helpers (one-token invocations, no
    //             quoting, no variables). The LLM stops authoring
    //             shell — it picks helpers from the table. The Doctor
    //             does the same: its remediation patches are helper
    //             invocations, never raw shell. The Verifier's
    //             allowlist becomes meaningful (no more nested-quote
    //             smuggling). End-to-end this collapses the failure
    //             surface for QUALSIASI repo (Node / Python / .NET /
    //             Bicep / mono-repo) to a small enumerable set of
    //             upstream tool failures, which the Doctor can either
    //             patch (different helper) or escalate ([Escalate]
    //             verdict on the repo source).
    //   v17 -> v18: bake `/usr/local/bin/agentic-azd-deploy`. The plain
    //             'azd deploy --no-prompt' fails with `resource not
    //             found: 0 resource groups with prefix or suffix with
    //             value: '<env-name>'` whenever the template's Bicep
    //             didn't output AZURE_RESOURCE_GROUP into the azd env
    //             (azd then falls back to a tag-scan on `azd-env-name`,
    //             which fails on RGs that pre-existed or were created
    //             with different tags). The new helper runs a pre-flight
    //             that discovers the RG by tag OR derives it from
    //             AZURE_RESOURCE_GROUP / AZD_RESOURCE_GROUP / common
    //             defaults, persists it into the azd env via
    //             `azd env set`, and only then invokes
    //             `azd deploy --no-prompt`. Idempotent and safe to call
    //             multiple times. Single source of truth for every
    //             post-`azd up` deploy step.
    private const string LocalTag = "agentichub/sandbox:v35";

    // Azure Linux (Mariner)-based azure-cli is multi-arch and ships with bash
    // and curl. We install the system toolchain via tdnf (tar, git, python,
    // jq, zip, unzip) plus .NET SDKs 8+9 (needed so azd's packaging phase
    // can invoke dotnet user-secrets / restore / build without downloading
    // an SDK at deploy time to a potentially read-only or noexec-mounted
    // /workspace - we've hit both cases on arm64 Docker Desktop), then
    // install Node.js 20 LTS and the Docker CLI from their official
    // static distributions (multi-arch, pinned versions).
    private const string Dockerfile =
        // CRITICAL: enables BuildKit heredoc support for `RUN cat > f <<'EOF'`.
        // Without this directive the legacy parser silently produces 0-byte
        // helper files (cat with no stdin) — the entire agentic-* toolbox
        // becomes empty stubs and every step exits 0 with no output, which
        // looks like 'success' to the orchestrator. See v19 fix.
        "# syntax=docker/dockerfile:1.7\n" +
        "FROM mcr.microsoft.com/azure-cli:latest\n" +
        "ARG TARGETARCH\n" +
        "RUN tdnf install -y tar ca-certificates git python3-pip jq zip unzip gawk sed grep coreutils " +
        "    dotnet-sdk-8.0 dotnet-sdk-9.0 && tdnf clean all\n" +
        // Node 22 LTS (Jod) from nodejs.org - platform-agnostic, pinned
        // version. Bumped from 20.18 → 22.12 because modern Azure samples
        // (azure-ai-travel-agents, agent-framework starter kits, several
        // langchain.js / openai-agents templates) declare
        // `"engines": { "node": "^20.19.0 || ^22.12.0 || >=23" }` and
        // 20.18 was triggering a sea of EBADENGINE warnings that confused
        // the Doctor. 22 LTS is the safe long-term floor: every package
        // that supports Node 20.19 also supports Node 22.
        "ENV NODE_VERSION=22.12.0\n" +
        "RUN set -eux; \\\n" +
        "    case \"${TARGETARCH:-$(uname -m)}\" in \\\n" +
        "      amd64|x86_64) NODE_ARCH=x64 ;; \\\n" +
        "      arm64|aarch64) NODE_ARCH=arm64 ;; \\\n" +
        "      *) echo \"unsupported arch: ${TARGETARCH:-$(uname -m)}\" && exit 1 ;; \\\n" +
        "    esac; \\\n" +
        "    curl -fsSL \"https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-${NODE_ARCH}.tar.gz\" -o /tmp/node.tgz; \\\n" +
        "    tar -xzf /tmp/node.tgz -C /usr/local --strip-components=1; \\\n" +
        "    rm /tmp/node.tgz; \\\n" +
        "    node --version; npm --version\n" +
        // Docker CLI (client only) from the official static-binary endpoint.
        // The server runs on the host and is reached through the socket we
        // bind-mount at container start. The download endpoint uses
        // 'x86_64' / 'aarch64' (not 'amd64' / 'arm64' like TARGETARCH).
        "ENV DOCKER_VERSION=27.4.0\n" +
        "RUN set -eux; \\\n" +
        "    case \"$(uname -m)\" in \\\n" +
        "      x86_64)  DOCKER_ARCH=x86_64  ;; \\\n" +
        "      aarch64) DOCKER_ARCH=aarch64 ;; \\\n" +
        "      *) echo \"unsupported arch for docker CLI: $(uname -m)\" && exit 1 ;; \\\n" +
        "    esac; \\\n" +
        "    curl -fsSL \"https://download.docker.com/linux/static/stable/${DOCKER_ARCH}/docker-${DOCKER_VERSION}.tgz\" -o /tmp/docker.tgz; \\\n" +
        "    tar -xzf /tmp/docker.tgz -C /tmp; \\\n" +
        "    install -m 0755 /tmp/docker/docker /usr/local/bin/docker; \\\n" +
        "    rm -rf /tmp/docker /tmp/docker.tgz; \\\n" +
        "    docker --version\n" +
        // buildx plugin � modern Dockerfiles depend on BuildKit features
        // like '--mount=type=cache', '--mount=type=secret', multi-stage
        // targeted builds. Without buildx, 'docker build' falls back to
        // the legacy builder which rejects those directives as syntax
        // errors. The plugin is an OCI-distributed binary; pin the
        // version to avoid surprises from the 'latest' channel.
        "ENV BUILDX_VERSION=v0.18.0\n" +
        "RUN set -eux; \\\n" +
        "    case \"$(uname -m)\" in \\\n" +
        "      x86_64)  BUILDX_ARCH=linux-amd64 ;; \\\n" +
        "      aarch64) BUILDX_ARCH=linux-arm64 ;; \\\n" +
        "      *) echo \"unsupported arch for buildx: $(uname -m)\" && exit 1 ;; \\\n" +
        "    esac; \\\n" +
        // System-wide plugin path (NOT /root/.docker/cli-plugins) so the
        // persistent docker-config volume mounted at /root/.docker at
        // runtime doesn't shadow the plugins. Docker CLI searches both
        // /usr/libexec/docker/cli-plugins and /usr/local/lib/docker/
        // cli-plugins as fallbacks.
        "    mkdir -p /usr/libexec/docker/cli-plugins; \\\n" +
        "    curl -fsSL \"https://github.com/docker/buildx/releases/download/${BUILDX_VERSION}/buildx-${BUILDX_VERSION}.${BUILDX_ARCH}\" \\\n" +
        "        -o /usr/libexec/docker/cli-plugins/docker-buildx; \\\n" +
        "    chmod +x /usr/libexec/docker/cli-plugins/docker-buildx; \\\n" +
        "    docker buildx version\n" +
        // docker compose v2 plugin + legacy 'docker-compose' shim.
        // Sample repos still ship hooks that call the hyphenated form
        // (e.g. azure-ai-travel-agents postprovision MCP setup runs
        // 'docker-compose -f file.yml build'); without the binary the
        // shell's word-splitting makes the `-f` look like a docker
        // flag, the docker CLI dumps its help text and azd up exits 1.
        // Bumped to v5.1.3 (latest stable on the docker/compose line —
        // the project skipped 3.x/4.x to avoid confusion with the legacy
        // file-format versions; v5 is just continuation of v2.40.x).
        // Required for `provider:` and `models:` top-level keys used by
        // current azd templates (azure-ai-travel-agents, agent-framework
        // starter kits). Older releases (<= v2.31) reject those as
        // `services.X Additional property provider is not allowed` and
        // azd's preflight bails out before any service can build.
        "ENV COMPOSE_VERSION=v5.1.3\n" +
        "RUN set -eux; \\\n" +
        "    case \"$(uname -m)\" in \\\n" +
        "      x86_64)  COMPOSE_ARCH=linux-x86_64 ;; \\\n" +
        "      aarch64) COMPOSE_ARCH=linux-aarch64 ;; \\\n" +
        "      *) echo \"unsupported arch for compose: $(uname -m)\" && exit 1 ;; \\\n" +
        "    esac; \\\n" +
        "    curl -fsSL \"https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-${COMPOSE_ARCH}\" \\\n" +
        "        -o /usr/libexec/docker/cli-plugins/docker-compose; \\\n" +
        "    chmod +x /usr/libexec/docker/cli-plugins/docker-compose; \\\n" +
        "    printf '%s\\n' '#!/bin/sh' 'exec docker compose \"$@\"' \\\n" +
        "        > /usr/local/bin/docker-compose; \\\n" +
        "    chmod +x /usr/local/bin/docker-compose; \\\n" +
        "    docker compose version; docker-compose version\n" +
        // Enable BuildKit by default. The host daemon (Docker Desktop)
        // supports it natively; this env var just tells the client to
        // ask for it. Covers RUN --mount=type=cache / secret / ssh / bind.
        "ENV DOCKER_BUILDKIT=1\n" +
        "ENV BUILDKIT_PROGRESS=plain\n" +
        "RUN pip3 install --no-cache-dir uv\n" +
        "RUN curl -fsSL https://aka.ms/install-azd.sh | bash\n" +
        "RUN azd config set auth.useAzCliAuth true\n" +
        // Pre-compile the azure-cli Python tree to .pyc bytecode. Saves
        // 6-10 seconds on the FIRST 'az' invocation inside a new sandbox
        // (the one that would otherwise be triggered by azd's credential
        // chain and timeout out at 10 s). 'compileall' errors from
        // optional/test modules are ignored on purpose. Also warms the
        // site-packages path so subsequent imports hit the disk cache.
        "RUN python3 -B -m compileall -q -f " +
        "    /usr/lib/python3*/site-packages/azure 2>/dev/null || true; \\\n" +
        "    python3 -B -m compileall -q -f " +
        "    /opt/az/lib/python3*/site-packages 2>/dev/null || true; \\\n" +
        "    az --version >/dev/null 2>&1 || true\n" +
        "ENV AZURE_DEV_COLLECT_TELEMETRY=no\n" +
        "ENV AZD_DEBUG_NO_UPDATE_CHECK=true\n" +
        // Suppress .NET first-run banner + telemetry so 'dotnet' calls
        // during azd package hooks stay silent.
        "ENV DOTNET_NOLOGO=true\n" +
        "ENV DOTNET_CLI_TELEMETRY_OPTOUT=true\n" +
        // /usr/local/bin/relocate-node-modules — preventive fix for the
        // Windows + Docker Desktop NOEXEC bind-mount problem on /workspace.
        // Called once before 'azd up' (preventive Strategist step) AND
        // again as the last-resort canonical fix after Doctor budget
        // exhaustion (DeploymentOrchestrator.LastResortRelocate). Keeping
        // the script BAKED into the image is the only way to give the
        // LLM a single-token preventive command — the previous multi-
        // line bash template (mapfile + ${var:-default} + nested \") was
        // mis-quoted by the LLM ~100% of the time, making Step 14 fail
        // with `mkdir: cannot create directory ''` on every Node deploy.
        // Heredoc 'BAKED_SCRIPT' is single-quoted so NOTHING is expanded
        // at image-build time; the script lands verbatim on disk.
        "RUN cat > /usr/local/bin/relocate-node-modules <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Relocate node_modules of every package.json under <root> (default:\n" +
        "# /workspace) onto exec-capable /tmp via symlink. Idempotent.\n" +
        "set -euo pipefail\n" +
        "root=\"${1:-/workspace}\"\n" +
        "if [ ! -d \"$root\" ]; then\n" +
        "  echo \"relocate-node-modules: root '$root' does not exist\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "count=0\n" +
        "while IFS= read -r -d '' pj; do\n" +
        "  d=\"$(dirname \"$pj\")\"\n" +
        "  # Only relocate if node_modules ALREADY exists as a real\n" +
        "  # populated directory (created by a prior 'npm install').\n" +
        "  # Creating a symlink where there was nothing breaks subsequent\n" +
        "  # 'docker build' COPY . . steps (BuildKit cache mount conflict)\n" +
        "  # AND is pointless because no binary needs +x yet.\n" +
        "  nm=\"$d/node_modules\"\n" +
        "  if [ ! -d \"$nm\" ] || [ -L \"$nm\" ]; then\n" +
        "    continue\n" +
        "  fi\n" +
        "  rel=\"${d#$root/}\"\n" +
        "  [ \"$rel\" = \"$d\" ] && rel=root\n" +
        "  slug=\"$(printf %s \"$rel\" | tr / _)\"\n" +
        "  [ -z \"$slug\" ] && slug=root\n" +
        "  tgt=\"/tmp/nm-$slug\"\n" +
        "  mkdir -p \"$tgt\"\n" +
        "  # Move existing contents into target volume (preserve install).\n" +
        "  if [ \"$(ls -A \"$nm\" 2>/dev/null)\" ]; then\n" +
        "    cp -a \"$nm\"/. \"$tgt\"/ 2>/dev/null || true\n" +
        "  fi\n" +
        "  rm -rf \"$nm\"\n" +
        "  ln -sfn \"$tgt\" \"$nm\"\n" +
        "  echo \"  -> $nm -> $tgt\"\n" +
        "  count=$((count + 1))\n" +
        "done < <(find \"$root\" -maxdepth 5 -name package.json \\\n" +
        "  -not -path '*/node_modules/*' -not -path '*/.git/*' -print0)\n" +
        "echo \"relocate-node-modules: relocated $count populated node_modules folder(s) under $root (skipped any without an existing install)\"\n" +
        "BAKED_SCRIPT\n" +
        "RUN chmod +x /usr/local/bin/relocate-node-modules \\\n" +
        " && /usr/local/bin/relocate-node-modules /tmp >/dev/null 2>&1 || true\n" +
        // ============================================================
        // v17 BAKED HELPERS — agentic-* toolbox.
        // Every helper:
        //   - lives in /usr/local/bin/<name> with mode 0755
        //   - uses 'set -euo pipefail'
        //   - emits a single tagged line per progress event:
        //       [<name>] ...
        //   - exits 0 ok / 2 user error / 3 env missing / 4 upstream
        //     tool error.
        // The Strategist + Doctor see the helper TABLE in their prompts
        // and emit invocations as ONE-TOKEN commands. No nested quoting,
        // no $var interpolation in the LLM-emitted commands.
        // Heredocs use single-quoted tags so $vars stay literal at
        // image build time.
        // ============================================================
        // -- relocate-venv ------------------------------------------------
        "RUN cat > /usr/local/bin/relocate-venv <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Relocate Python virtualenvs (.venv/venv) of every requirements.txt\n" +
        "# or pyproject.toml folder under <root> onto exec-capable /tmp.\n" +
        "# Mirrors relocate-node-modules but for Python. Idempotent.\n" +
        "set -euo pipefail\n" +
        "root=\"${1:-/workspace}\"\n" +
        "if [ ! -d \"$root\" ]; then\n" +
        "  echo \"[relocate-venv] root '$root' does not exist\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "count=0\n" +
        "while IFS= read -r -d '' marker; do\n" +
        "  d=\"$(dirname \"$marker\")\"\n" +
        "  rel=\"${d#$root/}\"\n" +
        "  [ \"$rel\" = \"$d\" ] && rel=root\n" +
        "  slug=\"$(printf %s \"$rel\" | tr / _)\"\n" +
        "  [ -z \"$slug\" ] && slug=root\n" +
        "  for venvname in .venv venv; do\n" +
        "    tgt=\"/tmp/venv-${slug}-${venvname}\"\n" +
        "    if [ -d \"$d/$venvname\" ] && [ ! -L \"$d/$venvname\" ]; then\n" +
        "      mkdir -p \"$tgt\"\n" +
        "      rm -rf \"$tgt\" && mv \"$d/$venvname\" \"$tgt\"\n" +
        "      ln -sfn \"$tgt\" \"$d/$venvname\"\n" +
        "      echo \"[relocate-venv]  -> $d/$venvname -> $tgt\"\n" +
        "      count=$((count + 1))\n" +
        "    fi\n" +
        "  done\n" +
        "done < <(find \"$root\" -maxdepth 5 \\( -name requirements.txt -o -name pyproject.toml \\) \\\n" +
        "  -not -path '*/node_modules/*' -not -path '*/.git/*' -print0)\n" +
        "echo \"[relocate-venv] processed $count venv(s) under $root\"\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-azd-env-prime ----------------------------------------
        "RUN cat > /usr/local/bin/agentic-azd-env-prime <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Reads /workspace/.env (or first arg) and forwards every K=V\n" +
        "# line to 'azd env set'. Skips comments + empty lines. Idempotent.\n" +
        "# Required when an azure.yaml uses ${VAR} placeholders that azd\n" +
        "# does not source from process env automatically (provision/\n" +
        "# deploy hooks).\n" +
        "set -euo pipefail\n" +
        "envfile=\"${1:-/workspace/.env}\"\n" +
        "if [ ! -f \"$envfile\" ]; then\n" +
        "  echo \"[agentic-azd-env-prime] no .env at $envfile, nothing to prime\"\n" +
        "  exit 0\n" +
        "fi\n" +
        "if ! command -v azd >/dev/null 2>&1; then\n" +
        "  echo \"[agentic-azd-env-prime] azd not on PATH\" >&2\n" +
        "  exit 4\n" +
        "fi\n" +
        "count=0\n" +
        "while IFS= read -r line || [ -n \"$line\" ]; do\n" +
        "  [[ \"$line\" =~ ^[[:space:]]*# ]] && continue\n" +
        "  [[ -z \"${line// }\" ]] && continue\n" +
        "  key=\"${line%%=*}\"\n" +
        "  val=\"${line#*=}\"\n" +
        "  key=\"${key// /}\"\n" +
        "  [[ -z \"$key\" ]] && continue\n" +
        "  val=\"${val%\\\"}\"; val=\"${val#\\\"}\"\n" +
        "  azd env set \"$key\" \"$val\" >/dev/null 2>&1 || true\n" +
        "  count=$((count + 1))\n" +
        "done < \"$envfile\"\n" +
        "echo \"[agentic-azd-env-prime] primed $count value(s) into azd env\"\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-azd-up -----------------------------------------------
        "RUN cat > /usr/local/bin/agentic-azd-up <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# AGENTIC AZD UP — split strategy.\n" +
        "# Step 1: 'azd provision --no-prompt' (Bicep only, no docker build).\n" +
        "# Step 2: for each service in azure.yaml that has a Dockerfile,\n" +
        "#         build REMOTELY via 'az acr build' and update the\n" +
        "#         matching Container App with 'az containerapp update'.\n" +
        "# Why: azd 1.24 has NO remote-build alpha for Container Apps;\n" +
        "# its in-process docker build hangs forever under qemu when the\n" +
        "# host is ARM (Apple Silicon, Windows-on-ARM) and the image is\n" +
        "# linux/amd64 (Angular, large Python). 'az acr build' runs natively\n" +
        "# in Azure (~2-4 min/svc) and avoids the qemu trap entirely.\n" +
        "set -euo pipefail\n" +
        "echo \"=========================================================\"\n" +
        "echo \"[agentic-azd-up v34] starting (sanitize azd .env + resolve CA by azd-service-name tag)\"\n" +
        "echo \"=========================================================\"\n" +
        "# Sanitize the azd env file BEFORE any azd call. azd refuses to load\n" +
        "# the env if a single line is malformed (e.g. JSON value injected\n" +
        "# without quoting, embedded newline, key starting with '{'). When that\n" +
        "# happens every azd call returns 'loading .env: unexpected character'\n" +
        "# and the deploy is wedged. Drop any line whose key is not a valid\n" +
        "# shell identifier; keep a .bak copy for forensics.\n" +
        "_sanitize_azd_env() {\n" +
        "  local envfile rc\n" +
        "  if [ ! -d .azure ]; then return 0; fi\n" +
        "  while IFS= read -r envfile; do\n" +
        "    [ -f \"$envfile\" ] || continue\n" +
        "    if awk -F= 'BEGIN{bad=0} /^[[:space:]]*$/{next} /^[[:space:]]*#/{next} { if ($1 !~ /^[A-Za-z_][A-Za-z0-9_]*$/) { bad=1; exit } } END{ exit bad }' \"$envfile\"; then\n" +
        "      continue\n" +
        "    fi\n" +
        "    echo \"[agentic-azd-up] WARN azd env file $envfile is corrupted — quarantining bad lines\" >&2\n" +
        "    awk -F= '/^[[:space:]]*$/{next} /^[[:space:]]*#/{next} { if ($1 ~ /^[A-Za-z_][A-Za-z0-9_]*$/) print }' \"$envfile\" > \"$envfile.tmp\" || true\n" +
        "    cp \"$envfile\" \"$envfile.bak.$(date +%s)\" 2>/dev/null || true\n" +
        "    mv \"$envfile.tmp\" \"$envfile\"\n" +
        "    echo \"[agentic-azd-up] sanitized $envfile (kept $(wc -l < \"$envfile\") good line(s))\" >&2\n" +
        "  done < <(find .azure -mindepth 2 -maxdepth 3 -name .env -type f 2>/dev/null)\n" +
        "}\n" +
        "_sanitize_azd_env || true\n" +
        "agentic-azd-env-prime /workspace/.env || true\n" +
        "if ! azd env get-value AZURE_LOCATION >/dev/null 2>&1; then\n" +
        "  [ -n \"${AZURE_LOCATION:-}\" ] && azd env set AZURE_LOCATION \"$AZURE_LOCATION\" || { echo \"[agentic-azd-up] AZURE_LOCATION missing\" >&2; exit 3; }\n" +
        "fi\n" +
        "if ! azd env get-value AZURE_SUBSCRIPTION_ID >/dev/null 2>&1 && [ -n \"${AZURE_SUBSCRIPTION_ID:-}\" ]; then\n" +
        "  azd env set AZURE_SUBSCRIPTION_ID \"$AZURE_SUBSCRIPTION_ID\"\n" +
        "fi\n" +
        "echo \"[agentic-azd-up] === phase 1: azd provision --no-prompt ===\"\n" +
        "azd provision --no-prompt\n" +
        "echo \"[agentic-azd-up] === phase 2: per-service remote ACR build + containerapp update ===\"\n" +
        "# Robust RG/ACR resolution: dump full azd env, then try keys, then tag, then pattern match.\n" +
        "envdump=\"$(azd env get-values 2>/dev/null || true)\"\n" +
        "_pluck() { echo \"$envdump\" | awk -F= -v k=\"$1\" '$1==k{ sub(/^\"/,\"\",$2); sub(/\"$/,\"\",$2); print $2; exit }'; }\n" +
        "rg=\"$(_pluck AZURE_RESOURCE_GROUP)\"\n" +
        "[ -z \"$rg\" ] && rg=\"$(_pluck AZURE_RESOURCE_GROUP_NAME)\"\n" +
        "[ -z \"$rg\" ] && rg=\"$(_pluck RESOURCE_GROUP_NAME)\"\n" +
        "[ -z \"$rg\" ] && rg=\"${AZURE_RESOURCE_GROUP:-${AZURE_RESOURCE_GROUP_NAME:-}}\"\n" +
        "env_name=\"$(_pluck AZURE_ENV_NAME)\"\n" +
        "[ -z \"$env_name\" ] && env_name=\"${AZURE_ENV_NAME:-}\"\n" +
        "if [ -z \"$rg\" ] && [ -n \"$env_name\" ]; then\n" +
        "  rg=\"$(az group list --tag azd-env-name=\"$env_name\" --query '[0].name' -o tsv 2>/dev/null || true)\"\n" +
        "fi\n" +
        "if [ -z \"$rg\" ] && [ -n \"$env_name\" ]; then\n" +
        "  rg=\"$(az group list --query \"[?starts_with(name,'rg-${env_name}')].name | [0]\" -o tsv 2>/dev/null || true)\"\n" +
        "fi\n" +
        "if [ -z \"$rg\" ]; then echo \"[agentic-azd-up] cannot resolve resource group (env_name=$env_name) — full azd env follows:\" >&2; echo \"$envdump\" >&2; exit 3; fi\n" +
        "echo \"[agentic-azd-up] resource group=$rg (env_name=$env_name)\"\n" +
        "# ACR resolution: env keys, then by RG, then by tag.\n" +
        "acr=\"$(_pluck AZURE_CONTAINER_REGISTRY_NAME)\"\n" +
        "[ -z \"$acr\" ] && acr=\"$(_pluck AZURE_CONTAINER_REGISTRY_ENDPOINT | sed 's/\\.azurecr\\.io.*$//')\"\n" +
        "[ -z \"$acr\" ] && acr=\"$(az acr list --resource-group \"$rg\" --query '[0].name' -o tsv 2>/dev/null || true)\"\n" +
        "if [ -z \"$acr\" ] && [ -n \"$env_name\" ]; then\n" +
        "  acr=\"$(az acr list --query \"[?tags.\\\"azd-env-name\\\"=='$env_name'].name | [0]\" -o tsv 2>/dev/null || true)\"\n" +
        "fi\n" +
        "if [ -z \"$acr\" ]; then echo \"[agentic-azd-up] no ACR found in $rg or by env_name=$env_name\" >&2; exit 4; fi\n" +
        "echo \"[agentic-azd-up] acr=$acr\"\n" +
        "# Find azure.yaml (default location: project root or /workspace)\n" +
        "azfile=\"$(azd env get-value AZURE_YAML_PATH 2>/dev/null || true)\"\n" +
        "for cand in \"$azfile\" /workspace/azure.yaml /workspace/azd/azure.yaml; do\n" +
        "  if [ -n \"$cand\" ] && [ -f \"$cand\" ]; then azfile=\"$cand\"; break; fi\n" +
        "done\n" +
        "if [ ! -f \"$azfile\" ]; then echo \"[agentic-azd-up] FATAL: azure.yaml not found in /workspace\" >&2; exit 5; fi\n" +
        "azroot=\"$(dirname \"$azfile\")\"\n" +
        "echo \"[agentic-azd-up] azure.yaml=$azfile\"\n" +
        "# Pure-awk parser for azure.yaml services (no pyyaml dependency).\n" +
        "# Format expected (azd convention):\n" +
        "#   services:\n" +
        "#     <name>:\n" +
        "#       project: <relpath>\n" +
        "#       host: <type>\n" +
        "#       docker:\n" +
        "#         path: <relpath>\n" +
        "#         context: <relpath>\n" +
        "# Output: TSV name\\tproject\\thost\\tdocker_path\\tdocker_context\n" +
        "_parse_services() {\n" +
        "  awk '\n" +
        "    function strip(s){ gsub(/^[ \\t]+|[ \\t]+$/, \"\", s); gsub(/^\"|\"$/,\"\",s); gsub(/^'\\''|'\\''$/,\"\",s); return s }\n" +
        "    function flush(){ if (svc!=\"\") print svc\"\\t\"project\"\\t\"host\"\\t\"dpath\"\\t\"dctx; svc=\"\";project=\"\";host=\"\";dpath=\"\";dctx=\"\";in_docker=0 }\n" +
        "    BEGIN { in_services=0; svc=\"\"; in_docker=0 }\n" +
        "    /^services:[[:space:]]*$/ { in_services=1; next }\n" +
        "    in_services && /^[^[:space:]#]/ { flush(); in_services=0; next }\n" +
        "    !in_services { next }\n" +
        "    /^  [A-Za-z0-9_.-]+:[[:space:]]*$/ { flush(); s=$0; sub(/^  /,\"\",s); sub(/:[[:space:]]*$/,\"\",s); svc=s; next }\n" +
        "    /^    project:/ { sub(/^    project:[[:space:]]*/,\"\"); project=strip($0); in_docker=0; next }\n" +
        "    /^    host:/ { sub(/^    host:[[:space:]]*/,\"\"); host=strip($0); in_docker=0; next }\n" +
        "    /^    docker:[[:space:]]*$/ { in_docker=1; next }\n" +
        "    /^      path:/ && in_docker { sub(/^      path:[[:space:]]*/,\"\"); dpath=strip($0); next }\n" +
        "    /^      context:/ && in_docker { sub(/^      context:[[:space:]]*/,\"\"); dctx=strip($0); next }\n" +
        "    /^    [A-Za-z]/ { in_docker=0 }\n" +
        "    END { flush() }\n" +
        "  ' \"$1\"\n" +
        "}\n" +
        "failed=()\n" +
        "succeeded=()\n" +
        "declare -A fail_reasons\n" +
        "_fail() { failed+=(\"$1\"); fail_reasons[\"$1\"]=\"$2\"; }\n" +
        "svc_count=0\n" +
        "while IFS=$'\\t' read -r svc proj host docker_path docker_context; do\n" +
        "  [ -z \"$svc\" ] && continue\n" +
        "  svc_count=$((svc_count+1))\n" +
        "  echo \"[agentic-azd-up] --- service: $svc (project=$proj host=$host docker_path=$docker_path docker_context=$docker_context) ---\"\n" +
        "  # azd convention: project is relative to azure.yaml dir (azroot).\n" +
        "  # docker.context AND docker.path are both relative to PROJECT (not azroot).\n" +
        "  proj_dir=\"$azroot/$proj\"\n" +
        "  if [ -n \"$docker_context\" ]; then ctx=\"$proj_dir/$docker_context\"; else ctx=\"$proj_dir\"; fi\n" +
        "  ctx=\"$(cd \"$ctx\" 2>/dev/null && pwd || echo \"$ctx\")\"\n" +
        "  if [ -n \"$docker_path\" ]; then\n" +
        "    df_full=\"$proj_dir/$docker_path\"\n" +
        "  else\n" +
        "    if [ -f \"$proj_dir/Dockerfile.production\" ]; then df_full=\"$proj_dir/Dockerfile.production\";\n" +
        "    else df_full=\"$proj_dir/Dockerfile\"; fi\n" +
        "  fi\n" +
        "  df_dir=\"$(cd \"$(dirname \"$df_full\")\" 2>/dev/null && pwd || echo \"$(dirname \"$df_full\")\")\"\n" +
        "  df_full=\"$df_dir/$(basename \"$df_full\")\"\n" +
        "  if [ ! -f \"$df_full\" ]; then\n" +
        "    echo \"[agentic-azd-up] $svc: WARN no Dockerfile at $df_full — skipping\" >&2; _fail \"$svc\" \"no Dockerfile at $df_full\"; continue\n" +
        "  fi\n" +
        "  if [ ! -d \"$ctx\" ]; then\n" +
        "    echo \"[agentic-azd-up] $svc: WARN context dir not found: $ctx — skipping\" >&2; _fail \"$svc\" \"context dir not found: $ctx\"; continue\n" +
        "  fi\n" +
        "  # Auto-generate .dockerignore if missing/empty: huge node_modules / dist / .git\n" +
        "  # blow up the ACR upload (and trigger needless local builds).\n" +
        "  di=\"$ctx/.dockerignore\"\n" +
        "  if [ ! -s \"$di\" ]; then\n" +
        "    echo \"[agentic-azd-up] $svc: writing default .dockerignore at $di\"\n" +
        "    cat > \"$di\" <<DI_EOF\n" +
        "node_modules\n" +
        ".angular\n" +
        "dist\n" +
        "build\n" +
        ".next\n" +
        ".git\n" +
        ".venv\n" +
        "__pycache__\n" +
        "*.pyc\n" +
        "*.log\n" +
        "DI_EOF\n" +
        "  fi\n" +
        "  image=\"$svc:azd-$(date +%s)\"\n" +
        "  # 'az acr build' uses the CLASSIC docker builder — BuildKit-only\n" +
        "  # directives like `RUN --mount=type=cache,target=...` are rejected\n" +
        "  # ('the --mount option requires BuildKit'). The `# syntax=...`\n" +
        "  # frontend hint is silently ignored by the classic builder, so\n" +
        "  # injecting it doesn't help. Instead STRIP the leading\n" +
        "  # `--mount=...` token(s) from RUN lines: the cache/secret/bind\n" +
        "  # mounts are pure optimisations — without them the build is\n" +
        "  # slightly slower but functionally identical, and 100% portable\n" +
        "  # to the legacy builder. Idempotent: skips when no --mount tokens\n" +
        "  # are present.\n" +
        "  if grep -qE '^[[:space:]]*RUN[[:space:]]+--(mount|network|security)=' \"$df_full\"; then\n" +
        "    echo \"[agentic-azd-up] $svc: stripping BuildKit-only RUN flags (--mount/--network/--security) from $df_full for classic ACR builder\"\n" +
        "    cp -f \"$df_full\" \"$df_full.orig\" 2>/dev/null || true\n" +
        "    sed -E -i 's/^([[:space:]]*RUN[[:space:]]+)((--(mount|network|security)=[^[:space:]]+[[:space:]]+)+)/\\1/' \"$df_full\"\n" +
        "    # Also strip BuildKit-only `# syntax=docker/dockerfile:...` directive\n" +
        "    # if present — it's harmless on classic, but tidies the file.\n" +
        "  fi\n" +
        "  echo \"[agentic-azd-up] $svc: az acr build remote (registry=$acr image=$image ctx=$ctx df=$df_full)\"\n" +
        "  acr_log=\"/tmp/acr-${svc}.log\"\n" +
        "  if ! az acr build --registry \"$acr\" --image \"$image\" --file \"$df_full\" \"$ctx\" --platform linux/amd64 2>&1 | tee \"$acr_log\"; then\n" +
        "    echo \"[agentic-azd-up] $svc: ACR build FAILED — last 40 lines of $acr_log:\" >&2\n" +
        "    tail -n 40 \"$acr_log\" >&2 || true\n" +
        "    last40=\"$(tail -n 40 \"$acr_log\" 2>/dev/null | tr '\\n' ' ' | tr -s ' ' | cut -c1-1500)\"\n" +
        "    _fail \"$svc\" \"az acr build FAILED (ctx=$ctx df=$df_full) | last_log=$last40\"; continue\n" +
        "  fi\n" +
        "  svc_upper=\"$(echo \"$svc\" | tr '[:lower:]-' '[:upper:]_')\"\n" +
        "  # Resolve Container App name. azd env values are unreliable when the bicep\n" +
        "  # template uses non-standard output keys (and azd writes 'Suggestion:' text\n" +
        "  # to stdout on missing keys, contaminating command substitution). Go directly\n" +
        "  # to az containerapp list, with strict match strategies.\n" +
        "  # 1) PRIMARY: azd-service-name tag — this is what azd itself uses to map\n" +
        "  #    services to resources. Bicep templates set tags.azd-service-name='<svc>'\n" +
        "  #    on the containerapp resource, so the CA can be named anything (e.g.\n" +
        "  #    'ca-api-<uniqueString>') and we still find it. Required for samples\n" +
        "  #    like Azure-Samples/get-started-with-ai-agents where the bicep names\n" +
        "  #    the CA with a uniqueString and the service id is 'api_and_frontend'.\n" +
        "  ca_name=\"$(az containerapp list -g \"$rg\" --query \"[?tags.\\\"azd-service-name\\\"=='$svc'].name | [0]\" -o tsv 2>/dev/null | tr -d '[:space:]' || true)\"\n" +
        "  if [ -z \"$ca_name\" ]; then\n" +
        "    ca_name=\"$(az containerapp list -g \"$rg\" --query \"[?name=='$svc'].name | [0]\" -o tsv 2>/dev/null | tr -d '[:space:]' || true)\"\n" +
        "  fi\n" +
        "  if [ -z \"$ca_name\" ]; then\n" +
        "    ca_name=\"$(az containerapp list -g \"$rg\" --query \"[?starts_with(name,'$svc')].name | [0]\" -o tsv 2>/dev/null | tr -d '[:space:]' || true)\"\n" +
        "  fi\n" +
        "  if [ -z \"$ca_name\" ]; then\n" +
        "    ca_name=\"$(az containerapp list -g \"$rg\" --query \"[?contains(name,'$svc')].name | [0]\" -o tsv 2>/dev/null | tr -d '[:space:]' || true)\"\n" +
        "  fi\n" +
        "  # 2) Last resort: services with underscores (e.g. 'api_and_frontend') often\n" +
        "  #    map to CAs with hyphens; try a sanitized variant.\n" +
        "  if [ -z \"$ca_name\" ]; then\n" +
        "    svc_alt=\"$(echo \"$svc\" | tr '_' '-')\"\n" +
        "    if [ \"$svc_alt\" != \"$svc\" ]; then\n" +
        "      ca_name=\"$(az containerapp list -g \"$rg\" --query \"[?contains(name,'$svc_alt')].name | [0]\" -o tsv 2>/dev/null | tr -d '[:space:]' || true)\"\n" +
        "    fi\n" +
        "  fi\n" +
        "  # Sanity: reject anything that looks like azd error/suggestion text.\n" +
        "  case \"$ca_name\" in *' '*|*ERROR*|*Suggestion*|*$'\\n'*) ca_name=\"\" ;; esac\n" +
        "  if [ -z \"$ca_name\" ]; then\n" +
        "    echo \"[agentic-azd-up] $svc: cannot resolve Container App name in $rg — skipping\" >&2; _fail \"$svc\" \"cannot resolve Container App name (rg=$rg)\"; continue\n" +
        "  fi\n" +
        "  echo \"[agentic-azd-up] $svc: az containerapp update --name $ca_name --image $acr.azurecr.io/$image\"\n" +
        "  caup_log=\"/tmp/caup-${svc}.log\"\n" +
        "  if az containerapp update --name \"$ca_name\" --resource-group \"$rg\" --image \"$acr.azurecr.io/$image\" >\"$caup_log\" 2>&1; then\n" +
        "    fqdn=\"$(az containerapp show --name \"$ca_name\" --resource-group \"$rg\" --query 'properties.configuration.ingress.fqdn' -o tsv 2>/dev/null || true)\"\n" +
        "    echo \"[agentic-azd-up] $svc: deployed -> https://$fqdn\"\n" +
        "    succeeded+=(\"$svc\")\n" +
        "  else\n" +
        "    echo \"[agentic-azd-up] $svc: containerapp update FAILED — last 40 lines of $caup_log:\" >&2\n" +
        "    tail -n 40 \"$caup_log\" >&2 || true\n" +
        "    caup_tail=\"$(tail -n 40 \"$caup_log\" 2>/dev/null | tr '\\n' ' ' | tr -s ' ' | cut -c1-1500)\"\n" +
        "    _fail \"$svc\" \"az containerapp update FAILED (ca=$ca_name image=$acr.azurecr.io/$image) | stderr=$caup_tail\"\n" +
        "  fi\n" +
        "done < <(_parse_services \"$azfile\")\n" +
        "echo \"[agentic-azd-up] === SUMMARY === parsed=$svc_count succeeded=${#succeeded[@]} failed=${#failed[@]}\"\n" +
        "echo \"[agentic-azd-up]   ok: ${succeeded[*]:-<none>}\"\n" +
        "echo \"[agentic-azd-up]   ko: ${failed[*]:-<none>}\"\n" +
        "if [ ${#failed[@]} -gt 0 ]; then\n" +
        "  echo \"[agentic-azd-up]   --- fail reasons ---\"\n" +
        "  for s in \"${failed[@]}\"; do\n" +
        "    echo \"[agentic-azd-up]     $s: ${fail_reasons[$s]:-<unknown>}\"\n" +
        "  done\n" +
        "fi\n" +
        "if [ $svc_count -eq 0 ]; then\n" +
        "  echo \"[agentic-azd-up] 0 services parsed from $azfile — hooks-only repo detected, falling back to azd deploy\"\n" +
        "  echo \"[agentic-azd-up] running: azd deploy --no-prompt\"\n" +
        "  deploy_log=\"/tmp/azd-deploy-fallback.log\"\n" +
        "  if azd deploy --no-prompt 2>&1 | tee \"$deploy_log\"; then\n" +
        "    echo \"[agentic-azd-up] azd deploy (hooks fallback) succeeded\"\n" +
        "    exit 0\n" +
        "  else\n" +
        "    deploy_rc=$?\n" +
        "    echo \"[agentic-azd-up] azd deploy (hooks fallback) FAILED rc=$deploy_rc — last 60 lines:\" >&2\n" +
        "    tail -n 60 \"$deploy_log\" >&2 || true\n" +
        "    echo \"[agentic-azd-up] === azure.yaml dump (first 200 lines) ===\" >&2\n" +
        "    head -n 200 \"$azfile\" >&2\n" +
        "    exit 6\n" +
        "  fi\n" +
        "fi\n" +
        "if [ ${#succeeded[@]} -eq 0 ]; then\n" +
        "  echo \"[agentic-azd-up] FATAL: 0 services successfully built/deployed.\" >&2; exit 7\n" +
        "fi\n" +
        "[ ${#failed[@]} -eq 0 ]\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-acr-build --------------------------------------------
        "RUN cat > /usr/local/bin/agentic-acr-build <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Remote ACR build (no Docker daemon, no local NOEXEC issues).\n" +
        "# Usage: agentic-acr-build <context-dir> <dockerfile-rel> <image-name>\n" +
        "#   <image-name> -> repo:tag (no registry); registry is auto-\n" +
        "#                   resolved from azd env AZURE_CONTAINER_REGISTRY_NAME\n" +
        "#                   (or AZURE_CONTAINER_REGISTRY_ENDPOINT).\n" +
        "set -euo pipefail\n" +
        "ctx=\"${1:-}\"; df=\"${2:-}\"; image=\"${3:-}\"\n" +
        "if [ -z \"$ctx\" ] || [ -z \"$df\" ] || [ -z \"$image\" ]; then\n" +
        "  echo \"[agentic-acr-build] usage: <context-dir> <dockerfile> <image:tag>\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "reg=\"$(azd env get-value AZURE_CONTAINER_REGISTRY_NAME 2>/dev/null || true)\"\n" +
        "if [ -z \"$reg\" ]; then\n" +
        "  ep=\"$(azd env get-value AZURE_CONTAINER_REGISTRY_ENDPOINT 2>/dev/null || true)\"\n" +
        "  reg=\"${ep%%.*}\"\n" +
        "fi\n" +
        "if [ -z \"$reg\" ]; then\n" +
        "  echo \"[agentic-acr-build] AZURE_CONTAINER_REGISTRY_NAME missing in azd env (provision did not run?)\" >&2\n" +
        "  exit 3\n" +
        "fi\n" +
        "echo \"[agentic-acr-build] registry=$reg image=$image context=$ctx dockerfile=$df\"\n" +
        "exec az acr build --registry \"$reg\" --image \"$image\" --file \"$df\" \"$ctx\"\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-build -------------------------------------------------
        "RUN cat > /usr/local/bin/agentic-build <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Build a docker image for a service. Tries LOCAL build with a\n" +
        "# strict timeout (default 8m). On timeout / failure, falls back\n" +
        "# to remote ACR build via agentic-acr-build.\n" +
        "# Usage: agentic-build <context-dir> <dockerfile> <image:tag> [<timeout-seconds>]\n" +
        "set -euo pipefail\n" +
        "ctx=\"${1:-}\"; df=\"${2:-}\"; image=\"${3:-}\"; tmo=\"${4:-480}\"\n" +
        "if [ -z \"$ctx\" ] || [ -z \"$df\" ] || [ -z \"$image\" ]; then\n" +
        "  echo \"[agentic-build] usage: <context-dir> <dockerfile> <image:tag> [timeout-seconds]\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "echo \"[agentic-build] attempting local docker build (timeout ${tmo}s) image=$image\"\n" +
        "if timeout --kill-after=10 \"${tmo}\" docker build -f \"$df\" -t \"$image\" \"$ctx\"; then\n" +
        "  echo \"[agentic-build] local build OK\"\n" +
        "  exit 0\n" +
        "fi\n" +
        "rc=$?\n" +
        "echo \"[agentic-build] local build failed/timed-out (rc=$rc), falling back to ACR remote build\"\n" +
        "exec agentic-acr-build \"$ctx\" \"$df\" \"$image\"\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-npm-install ------------------------------------------
        "RUN cat > /usr/local/bin/agentic-npm-install <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Robust npm install: prefers `npm ci` (lockfile-strict), falls\n" +
        "# back to `npm install --no-audit --no-fund` only if ci fails.\n" +
        "# Usage: agentic-npm-install <dir>\n" +
        "set -euo pipefail\n" +
        "dir=\"${1:-.}\"\n" +
        "if [ ! -f \"$dir/package.json\" ]; then\n" +
        "  echo \"[agentic-npm-install] no package.json in $dir\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "cd \"$dir\"\n" +
        "if [ -f package-lock.json ] || [ -f npm-shrinkwrap.json ]; then\n" +
        "  echo \"[agentic-npm-install] $dir: trying npm ci\"\n" +
        "  if npm ci --no-audit --no-fund; then exit 0; fi\n" +
        "  echo \"[agentic-npm-install] $dir: ci failed, retrying with npm install\"\n" +
        "fi\n" +
        "exec npm install --no-audit --no-fund\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-dotnet-restore ---------------------------------------
        "RUN cat > /usr/local/bin/agentic-dotnet-restore <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# 'dotnet restore' with retry. Many transient nuget feed timeouts\n" +
        "# succeed on the second attempt.\n" +
        "# Usage: agentic-dotnet-restore <csproj-or-sln>\n" +
        "set -euo pipefail\n" +
        "target=\"${1:-}\"\n" +
        "if [ -z \"$target\" ] || [ ! -f \"$target\" ]; then\n" +
        "  echo \"[agentic-dotnet-restore] target '$target' not found\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "for i in 1 2 3; do\n" +
        "  echo \"[agentic-dotnet-restore] attempt $i for $target\"\n" +
        "  if dotnet restore \"$target\" --no-cache; then exit 0; fi\n" +
        "  sleep $((i * 5))\n" +
        "done\n" +
        "echo \"[agentic-dotnet-restore] giving up after 3 attempts\" >&2\n" +
        "exit 4\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-bicep ------------------------------------------------
        "RUN cat > /usr/local/bin/agentic-bicep <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Build (lint+compile) a bicep file via 'az bicep build'.\n" +
        "# Ensures the bicep CLI is installed first.\n" +
        "# Usage: agentic-bicep <bicep-file>\n" +
        "set -euo pipefail\n" +
        "f=\"${1:-}\"\n" +
        "if [ -z \"$f\" ] || [ ! -f \"$f\" ]; then\n" +
        "  echo \"[agentic-bicep] file '$f' not found\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "az bicep install >/dev/null 2>&1 || true\n" +
        "echo \"[agentic-bicep] building $f\"\n" +
        "exec az bicep build --file \"$f\"\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-clone ------------------------------------------------
        "RUN cat > /usr/local/bin/agentic-clone <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# git clone --recursive with 3-attempt retry. Honors GIT_TERMINAL\n" +
        "# _PROMPT=0 so credentials never block CI.\n" +
        "# Usage: agentic-clone <url> <dest-dir>\n" +
        "set -euo pipefail\n" +
        "url=\"${1:-}\"; dst=\"${2:-}\"\n" +
        "if [ -z \"$url\" ] || [ -z \"$dst\" ]; then\n" +
        "  echo \"[agentic-clone] usage: <url> <dest-dir>\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "export GIT_TERMINAL_PROMPT=0\n" +
        "for i in 1 2 3; do\n" +
        "  echo \"[agentic-clone] attempt $i: $url -> $dst\"\n" +
        "  if git clone --recursive --depth 1 \"$url\" \"$dst\"; then exit 0; fi\n" +
        "  rm -rf \"$dst\"\n" +
        "  sleep $((i * 3))\n" +
        "done\n" +
        "exit 4\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-aca-wait ---------------------------------------------
        "RUN cat > /usr/local/bin/agentic-aca-wait <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Poll a Container App's provisioningState until Succeeded /\n" +
        "# Failed / Canceled, with a timeout.\n" +
        "# Usage: agentic-aca-wait <app-name> <resource-group> [<timeout-seconds>]\n" +
        "set -euo pipefail\n" +
        "app=\"${1:-}\"; rg=\"${2:-}\"; tmo=\"${3:-600}\"\n" +
        "if [ -z \"$app\" ] || [ -z \"$rg\" ]; then\n" +
        "  echo \"[agentic-aca-wait] usage: <app-name> <resource-group> [timeout]\" >&2\n" +
        "  exit 2\n" +
        "fi\n" +
        "deadline=$(( $(date +%s) + tmo ))\n" +
        "while [ $(date +%s) -lt $deadline ]; do\n" +
        "  state=\"$(az containerapp show --name \"$app\" --resource-group \"$rg\" --query 'properties.provisioningState' -o tsv 2>/dev/null || echo Unknown)\"\n" +
        "  echo \"[agentic-aca-wait] $app: $state\"\n" +
        "  case \"$state\" in\n" +
        "    Succeeded) exit 0 ;;\n" +
        "    Failed|Canceled) exit 4 ;;\n" +
        "  esac\n" +
        "  sleep 10\n" +
        "done\n" +
        "echo \"[agentic-aca-wait] timeout after ${tmo}s\" >&2\n" +
        "exit 4\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-summary ----------------------------------------------
        "RUN cat > /usr/local/bin/agentic-summary <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Print final deployment summary: azd env values + Container App\n" +
        "# FQDNs in the active resource group. Best-effort; never fails.\n" +
        "set +e\n" +
        "echo \"========== AGENTIC DEPLOY SUMMARY ==========\"\n" +
        "echo \"--- azd env get-values ---\"\n" +
        "azd env get-values 2>/dev/null || echo \"(azd env unavailable)\"\n" +
        "rg=\"$(azd env get-value AZURE_RESOURCE_GROUP 2>/dev/null || true)\"\n" +
        "if [ -n \"$rg\" ]; then\n" +
        "  echo \"--- Container Apps in $rg ---\"\n" +
        "  az containerapp list -g \"$rg\" --query '[].{name:name,fqdn:properties.configuration.ingress.fqdn,state:properties.provisioningState}' -o table 2>/dev/null || true\n" +
        "fi\n" +
        "echo \"============================================\"\n" +
        "exit 0\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-help -------------------------------------------------
        "RUN cat > /usr/local/bin/agentic-help <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "cat <<'HELP'\n" +
        "AGENTIC DEPLOY HELPERS (sandbox image v17)\n" +
        "==========================================\n" +
        "  relocate-node-modules <root>\n" +
        "      Move node_modules onto exec-capable /tmp via symlink.\n" +
        "  relocate-venv <root>\n" +
        "      Same idea for Python .venv / venv folders.\n" +
        "  agentic-azd-env-prime [<env-file>]\n" +
        "      Forward .env K=V lines into 'azd env set'.\n" +
        "  agentic-azd-up [-- args...]\n" +
        "      'azd up --no-prompt' with safe defaults + env priming.\n" +
        "  agentic-azd-deploy [-- args...]\n" +
        "      'azd deploy --no-prompt' with AZURE_RESOURCE_GROUP pre-resolved\n" +
        "      (by tag scan) so it doesn't fail with '0 resource groups'.\n" +
        "  agentic-acr-build <ctx> <dockerfile> <image:tag>\n" +
        "      Remote 'az acr build'; registry from azd env.\n" +
        "  agentic-build <ctx> <dockerfile> <image:tag> [tmo-sec]\n" +
        "      Local docker build, automatic ACR fallback on failure.\n" +
        "  agentic-npm-install <dir>\n" +
        "      'npm ci' with 'npm install' fallback.\n" +
        "  agentic-dotnet-restore <csproj-or-sln>\n" +
        "      'dotnet restore' with 3-attempt retry.\n" +
        "  agentic-bicep <bicep-file>\n" +
        "      'az bicep build' (installs bicep CLI if missing).\n" +
        "  agentic-clone <url> <dest>\n" +
        "      'git clone --recursive' with retry.\n" +
        "  agentic-aca-wait <app> <rg> [tmo-sec]\n" +
        "      Poll Container App provisioningState to Succeeded.\n" +
        "  agentic-summary\n" +
        "      Final azd outputs + Container App FQDNs.\n" +
        "HELP\n" +
        "BAKED_SCRIPT\n" +
        // -- agentic-azd-deploy --------------------------------------------
        "RUN cat > /usr/local/bin/agentic-azd-deploy <<'BAKED_SCRIPT'\n" +
        "#!/bin/bash\n" +
        "# Wrapper around 'azd deploy --no-prompt' that PRE-RESOLVES\n" +
        "# AZURE_RESOURCE_GROUP into the azd env when the template's\n" +
        "# Bicep didn't output it. Without this, 'azd deploy' falls back\n" +
        "# to scanning RGs by 'azd-env-name=<env>' tag and fails with\n" +
        "# 'resource not found: 0 resource groups with prefix or suffix\n" +
        "# with value: <env>'.\n" +
        "# Forwards extra args after '--' verbatim (e.g. service name).\n" +
        "set -euo pipefail\n" +
        "if ! command -v azd >/dev/null 2>&1; then\n" +
        "  echo \"[agentic-azd-deploy] azd not on PATH\" >&2; exit 4\n" +
        "fi\n" +
        "env_name=\"$(azd env get-value AZURE_ENV_NAME 2>/dev/null || true)\"\n" +
        "if [ -z \"$env_name\" ]; then\n" +
        "  env_name=\"$(azd env list --output json 2>/dev/null | jq -r '.[] | select(.IsDefault==true) | .Name' 2>/dev/null || true)\"\n" +
        "fi\n" +
        "echo \"[agentic-azd-deploy] active azd env: ${env_name:-<unknown>}\"\n" +
        "# Robust RG/ACR resolution: dump full azd env, try keys, then tag, then pattern.\n" +
        "envdump=\"$(azd env get-values 2>/dev/null || true)\"\n" +
        "_pluck() { echo \"$envdump\" | awk -F= -v k=\"$1\" '$1==k{ sub(/^\"/,\"\",$2); sub(/\"$/,\"\",$2); print $2; exit }'; }\n" +
        "rg=\"$(_pluck AZURE_RESOURCE_GROUP)\"\n" +
        "[ -z \"$rg\" ] && rg=\"$(_pluck AZURE_RESOURCE_GROUP_NAME)\"\n" +
        "[ -z \"$rg\" ] && rg=\"$(_pluck RESOURCE_GROUP_NAME)\"\n" +
        "[ -z \"$rg\" ] && rg=\"${AZURE_RESOURCE_GROUP:-${AZURE_RESOURCE_GROUP_NAME:-${AZD_RESOURCE_GROUP:-}}}\"\n" +
        "if [ -z \"$rg\" ] && [ -n \"$env_name\" ]; then\n" +
        "  rg=\"$(az group list --tag azd-env-name=\"$env_name\" --query '[0].name' -o tsv 2>/dev/null || true)\"\n" +
        "fi\n" +
        "if [ -z \"$rg\" ] && [ -n \"$env_name\" ]; then\n" +
        "  rg=\"$(az group list --query \"[?starts_with(name,'rg-${env_name}')].name | [0]\" -o tsv 2>/dev/null || true)\"\n" +
        "fi\n" +
        "if [ -z \"$rg\" ] && [ -n \"$env_name\" ]; then\n" +
        "  for cand in \"rg-$env_name\" \"$env_name\" \"rg-${env_name%%-ag-*}\"; do\n" +
        "    if [ -n \"$cand\" ] && az group show --name \"$cand\" >/dev/null 2>&1; then\n" +
        "      rg=\"$cand\"; break\n" +
        "    fi\n" +
        "  done\n" +
        "fi\n" +
        "if [ -z \"$rg\" ]; then\n" +
        "  echo \"[agentic-azd-deploy] could not resolve resource group for env '$env_name' — full azd env follows:\" >&2\n" +
        "  echo \"$envdump\" >&2\n" +
        "  exit 3\n" +
        "fi\n" +
        "echo \"[agentic-azd-deploy] resource group: $rg\"\n" +
        "azd env set AZURE_RESOURCE_GROUP \"$rg\" >/dev/null 2>&1 || true\n" +
        "# Idempotency check: if all Container Apps in $rg already have a non-\n" +
        "# placeholder image, skip the redundant azd deploy (which fails on\n" +
        "# Angular under qemu emulation). Placeholder = mcr.microsoft.com/.../helloworld.\n" +
        "placeholder_count=\"$(az containerapp list -g \"$rg\" --query \"length([?contains(properties.template.containers[0].image,'helloworld')])\" -o tsv 2>/dev/null || echo unknown)\"\n" +
        "total_count=\"$(az containerapp list -g \"$rg\" --query 'length([])' -o tsv 2>/dev/null || echo 0)\"\n" +
        "echo \"[agentic-azd-deploy] container apps in $rg: $total_count total, $placeholder_count still on placeholder image\"\n" +
        "if [ \"$placeholder_count\" = \"0\" ] && [ \"$total_count\" -gt \"0\" ]; then\n" +
        "  echo \"[agentic-azd-deploy] ✓ all $total_count container app(s) already have real images; skipping redundant azd deploy.\"\n" +
        "  exit 0\n" +
        "fi\n" +
        "echo \"[agentic-azd-deploy] starting azd deploy --no-prompt $*\"\n" +
        "exec azd deploy --no-prompt \"$@\"\n" +
        "BAKED_SCRIPT\n" +
        // chmod + smoke-test
        "RUN chmod +x /usr/local/bin/relocate-venv \\\n" +
        "      /usr/local/bin/agentic-azd-env-prime \\\n" +
        "      /usr/local/bin/agentic-azd-up \\\n" +
        "      /usr/local/bin/agentic-azd-deploy \\\n" +
        "      /usr/local/bin/agentic-acr-build \\\n" +
        "      /usr/local/bin/agentic-build \\\n" +
        "      /usr/local/bin/agentic-npm-install \\\n" +
        "      /usr/local/bin/agentic-dotnet-restore \\\n" +
        "      /usr/local/bin/agentic-bicep \\\n" +
        "      /usr/local/bin/agentic-clone \\\n" +
        "      /usr/local/bin/agentic-aca-wait \\\n" +
        "      /usr/local/bin/agentic-summary \\\n" +
        "      /usr/local/bin/agentic-help \\\n" +
        " && /usr/local/bin/agentic-help >/dev/null\n" +
        // Real smoke test: every helper MUST be non-empty AND have a
        // shebang. Catches the v18 bug where heredocs silently produced
        // 0-byte files and broke every deploy invisibly.
        "RUN set -e; for f in relocate-node-modules relocate-venv " +
        "agentic-azd-env-prime agentic-azd-up agentic-azd-deploy " +
        "agentic-acr-build agentic-build agentic-npm-install " +
        "agentic-dotnet-restore agentic-bicep agentic-clone " +
        "agentic-aca-wait agentic-summary agentic-help; do " +
        "  p=/usr/local/bin/$f; " +
        "  test -s \"$p\" || { echo \"BAKE-FAIL: $p is empty (heredoc broken)\" >&2; exit 1; }; " +
        "  head -c 2 \"$p\" | grep -q '#!' || { echo \"BAKE-FAIL: $p missing shebang\" >&2; exit 1; }; " +
        "  echo \"  ok $p $(wc -c < $p)B\"; " +
        "done\n";

    private static readonly SemaphoreSlim _sem = new(1, 1);
    private static string? _resolvedTag;

    /// <summary>
    /// Returns the image name that should be used for the sandbox. On supported
    /// architectures this is just the requested image; on arm64 hosts, if the
    /// requested image is the amd64-only azure-dev-cli-apps image, we swap in
    /// a locally-built multi-arch image (built on demand, then cached).
    /// </summary>
    public static async Task<string> ResolveAsync(
        string requestedImage,
        Action<string> log,
        CancellationToken ct)
    {
        // Only the well-known azure-dev-cli-apps reference is replaceable.
        // Anything else (custom user image) is passed through untouched.
        if (!requestedImage.Contains("azure-dev-cli-apps", StringComparison.OrdinalIgnoreCase))
            return requestedImage;

        // v32+: ALWAYS swap to our locally-built image regardless of host
        // architecture. The planner emits commands that rely on baked helpers
        // (relocate-node-modules, agentic-azd-up, agentic-azd-deploy, ...)
        // and those only exist in our image — the upstream
        // azure-dev-cli-apps image does not have them, so on amd64 hosts
        // the deploy used to fail at the first relocate-* step.
        // Building the image is cheap (~1-2 min, one-time-per-host).
        var dockerArch = await DetectDockerArchAsync(ct);
        log($"Docker daemon architecture: {dockerArch ?? "unknown"} " +
            "(swapping azure-dev-cli-apps -> local sandbox image with baked helpers).");

        if (_resolvedTag is not null) return _resolvedTag;

        await _sem.WaitAsync(ct);
        try
        {
            if (_resolvedTag is not null) return _resolvedTag;

            // If the local image already exists, reuse it.
            var inspect = await Cli.Wrap("docker")
                .WithArguments(new[] { "image", "inspect", LocalTag })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
            if (inspect.ExitCode == 0)
            {
                log($"Using existing local sandbox image '{LocalTag}'.");
                _resolvedTag = LocalTag;
                return LocalTag;
            }

            log($"Building local sandbox image '{LocalTag}' for arch '{dockerArch ?? "?"}' (first run only, ~1-2 min)...");

            var tempDir = Path.Combine(Path.GetTempPath(), "agentichub-sandbox-build");
            // Wipe any prior contents to avoid stale helper files.
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            // Convert every `RUN cat > /usr/local/bin/<name> <<'BAKED_SCRIPT'
            // ... BAKED_SCRIPT` heredoc block in our Dockerfile string into:
            //   1. a separate file written to the build context
            //      (helpers/<name>) with the script body (LF line endings)
            //   2. a `COPY helpers/<name> /usr/local/bin/<name>` line
            // The legacy Docker parser doesn't support RUN heredocs and the
            // daemon's BuildKit path needs the buildx plugin which isn't
            // available in our orchestrator's docker CLI. COPY is universal
            // and requires neither.
            string materialized = MaterializeHeredocsAsCopies(Dockerfile, tempDir, log);
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "Dockerfile"),
                materialized,
                new System.Text.UTF8Encoding(false),
                ct);

            int exit = -1;
            await foreach (var ev in Cli.Wrap("docker")
                .WithArguments(new[] { "build", "-t", LocalTag, tempDir })
                .WithValidation(CommandResultValidation.None)
                .ListenAsync(ct))
            {
                switch (ev)
                {
                    case StandardOutputCommandEvent o: log(o.Text); break;
                    case StandardErrorCommandEvent e:  log(e.Text); break;
                    case ExitedCommandEvent x:         exit = x.ExitCode; break;
                }
            }
            if (exit != 0)
                throw new InvalidOperationException(
                    $"Failed to build sandbox image '{LocalTag}' (docker build exit code {exit}). " +
                    "Check network connectivity and Docker Desktop resources.");

            log($"Sandbox image '{LocalTag}' built successfully.");
            _resolvedTag = LocalTag;
            return LocalTag;
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>
    /// Replaces every <c>RUN cat &gt; /path/&lt;name&gt; &lt;&lt;'BAKED_SCRIPT' ... BAKED_SCRIPT</c>
    /// block with <c>COPY helpers/&lt;name&gt; /path/&lt;name&gt;</c>, writing each
    /// heredoc body to <c>{tempDir}/helpers/&lt;name&gt;</c> with LF line
    /// endings. Strips any <c># syntax=docker/dockerfile:...</c> directive
    /// since we no longer rely on BuildKit features.
    /// </summary>
    private static string MaterializeHeredocsAsCopies(
        string dockerfile, string tempDir, Action<string> log)
    {
        var helpersDir = Path.Combine(tempDir, "helpers");
        Directory.CreateDirectory(helpersDir);

        // Strip the `# syntax=...` directive line if present (legacy
        // parser ignores it but emits noise in some Docker versions).
        var rx = new System.Text.RegularExpressions.Regex(
            @"^# syntax=[^\n]*\n",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        dockerfile = rx.Replace(dockerfile, string.Empty);

        // Match: RUN cat > <path> <<'BAKED_SCRIPT'\n<body>\nBAKED_SCRIPT\n
        var heredoc = new System.Text.RegularExpressions.Regex(
            @"RUN cat > (?<path>\S+) <<'BAKED_SCRIPT'\n(?<body>.*?)\nBAKED_SCRIPT\n",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        int count = 0;
        var result = heredoc.Replace(dockerfile, m =>
        {
            var path = m.Groups["path"].Value;
            var body = m.Groups["body"].Value;
            var name = Path.GetFileName(path);
            var rel = "helpers/" + name;
            // Ensure LF endings (already \n in our literal, but be safe).
            body = body.Replace("\r\n", "\n");
            File.WriteAllText(
                Path.Combine(helpersDir, name),
                body,
                new System.Text.UTF8Encoding(false));
            count++;
            return $"COPY {rel} {path}\n";
        });

        log($"Materialized {count} baked helper script(s) to build context " +
            $"({helpersDir}).");
        return result;
    }

    private static async Task<string?> DetectDockerArchAsync(CancellationToken ct)
    {
        try
        {
            var stdout = new System.Text.StringBuilder();
            var result = await Cli.Wrap("docker")
                .WithArguments(new[] { "version", "--format", "{{.Server.Arch}}" })
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
            if (result.ExitCode != 0) return null;
            var arch = stdout.ToString().Trim();
            return string.IsNullOrWhiteSpace(arch) ? null : arch;
        }
        catch
        {
            return null;
        }
    }
}
