<#
.SYNOPSIS
    One-shot bootstrap for AgentStationHub on any Windows / macOS / Linux
    laptop that has Docker Desktop installed.

.DESCRIPTION
    Walks the user from a cold repo clone to a running container with a
    browser tab open, doing all the plumbing in between:

      1. Detect the host (OS / arch / shell) and print a short summary.
      2. Verify Docker Desktop is reachable. If not, offer to start it on
         Windows and wait for the daemon to come up.
      3. Verify the Azure CLI is installed and the user has a default
         subscription; run 'az login' interactively if missing.
      4. Create .env from .env.example if it does not exist, and prompt
         for the three Azure OpenAI values (endpoint + two deployment
         names). Sensible defaults are offered.
      5. Pull / build the image and launch the stack via 'docker compose
         up --build -d'.
      6. Poll http://localhost:8080/hub until it answers 200 OK (up to
         120 s), then open the browser on the user's default handler.

    The script is IDEMPOTENT: re-running it on a machine that is already
    configured just re-starts the stack and opens the browser.

    All user-facing output uses pure-ASCII glyphs ([OK], [X], [!], *, ===)
    so it renders correctly on PowerShell 5.1 / cmd.exe / legacy terminals
    that default to CP437 / CP1252 and garble box-drawing characters.

.EXAMPLE
    # Default path: interactive first-time setup.
    pwsh .\start.ps1

.EXAMPLE
    # Re-launch without rebuilding the image (faster on warm runs):
    pwsh .\start.ps1 -NoBuild

.EXAMPLE
    # Headless / CI mode: fail instead of prompting when .env is missing.
    pwsh .\start.ps1 -NonInteractive
#>

[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $Reset,
    [switch] $Purge,
    [switch] $NoBrowser,
    [switch] $NonInteractive,
    # When set, skip the base-image pull. Faster restart (~5-10 s saved)
    # but you'll keep running whatever digest Docker last cached for
    # mcr.microsoft.com/dotnet/{sdk,aspnet}:8.0. Use only when you know
    # nothing upstream changed � everyday 'always latest' should leave
    # this off so start.ps1 does 'docker compose build --pull' and
    # always fetches fresh base layers.
    [switch] $NoPull,
    [int] $Port = 8080
)

$ErrorActionPreference = 'Stop'
# Silence the giant "Reading web response / Reading response stream"
# progress pane that PowerShell 5.1 shows during Invoke-WebRequest �
# it overwrites the nicely formatted section output with cursor magic.
$ProgressPreference   = 'SilentlyContinue'

# --------------------------------------------------------------------------
# Helpers (ASCII-only output - renders on every shell/terminal)
# --------------------------------------------------------------------------

function Write-Section([string] $Title) {
    Write-Host ""
    Write-Host ("=== {0} " -f $Title).PadRight(78, '=') -ForegroundColor Cyan
}

function Write-Ok  ([string] $Msg) { Write-Host "  [OK] $Msg" -ForegroundColor Green }
function Write-Info([string] $Msg) { Write-Host "  *    $Msg" -ForegroundColor Gray }
function Write-Warn([string] $Msg) { Write-Host "  [!]  $Msg" -ForegroundColor Yellow }
function Write-Err ([string] $Msg) { Write-Host "  [X]  $Msg" -ForegroundColor Red }

# Run a command with a timeout (PowerShell Jobs). Returns ok=$true if the
# command completed successfully before the timeout, ok=$false otherwise.
# Use for anything that can hang indefinitely (e.g. 'docker version' when
# the daemon is half-started).
function Invoke-WithTimeout {
    param([scriptblock] $Script, [int] $Seconds = 10)
    $job = Start-Job -ScriptBlock $Script
    if (Wait-Job $job -Timeout $Seconds) {
        $out = Receive-Job $job 2>&1
        Remove-Job $job -Force
        return @{ ok = ($job.ChildJobs[0].JobStateInfo.State -eq 'Completed'); out = $out }
    }
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    return @{ ok = $false; out = "timeout after $Seconds s" }
}

# --------------------------------------------------------------------------
# 1. Host + repo sanity
# --------------------------------------------------------------------------

Write-Section 'Environment'
$isWindowsHost = $IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop')
$osName = if ($isWindowsHost) { 'Windows' } elseif ($IsMacOS) { 'macOS' } else { 'Linux' }
Write-Info "Host OS     : $osName"
Write-Info "PowerShell  : $($PSVersionTable.PSVersion)"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot
Write-Info "Repo root   : $repoRoot"

$requiredFiles = @('Dockerfile', 'docker-compose.yml', 'entrypoint.sh', '.env.example')
foreach ($f in $requiredFiles) {
    if (-not (Test-Path (Join-Path $repoRoot $f))) {
        Write-Err "Missing '$f' in repo root. Are you running this from the right folder?"
        exit 1
    }
}
Write-Ok "All required files present ($($requiredFiles -join ', '))."

# --------------------------------------------------------------------------
# 2. Docker Desktop
# --------------------------------------------------------------------------

Write-Section 'Docker'

$dockerCli = Get-Command docker -ErrorAction SilentlyContinue
if (-not $dockerCli) {
    Write-Err "'docker' not found on PATH. Install Docker Desktop from https://www.docker.com/products/docker-desktop and retry."
    exit 1
}
Write-Ok "docker CLI found ($($dockerCli.Source))."

$r = Invoke-WithTimeout -Script { docker version --format '{{.Server.Version}}' 2>&1 } -Seconds 8
if ($r.ok -and $r.out -match '^\d+\.\d+') {
    Write-Ok "Docker daemon reachable (server $($r.out))."
}
else {
    Write-Warn "Docker daemon not responding. Attempting to start Docker Desktop..."

    if ($isWindowsHost) {
        $dd = @(
            "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe",
            "$env:LOCALAPPDATA\Docker\Docker Desktop.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $dd) {
            Write-Err "Docker Desktop executable not found. Install it from https://www.docker.com/products/docker-desktop."
            exit 1
        }

        Start-Process -FilePath $dd | Out-Null
        Write-Info "Launched $dd. Waiting up to 90 s for the engine..."
    }
    elseif ($IsMacOS) {
        Start-Process -FilePath '/Applications/Docker.app' | Out-Null
        Write-Info "Launched Docker.app. Waiting up to 90 s for the engine..."
    }
    else {
        Write-Err "Please start the Docker daemon (e.g. 'sudo systemctl start docker') and retry."
        exit 1
    }

    $ready = $false
    for ($i = 1; $i -le 18; $i++) {
        $r = Invoke-WithTimeout -Script { docker version --format '{{.Server.Version}}' 2>&1 } -Seconds 5
        if ($r.ok -and $r.out -match '^\d+\.\d+') {
            Write-Ok "Docker daemon came up (server $($r.out))."
            $ready = $true
            break
        }
        Write-Info "still starting... ($i/18)"
        Start-Sleep -Seconds 5
    }
    if (-not $ready) {
        Write-Err "Docker daemon did not become reachable within 90 s. Open Docker Desktop manually and retry."
        exit 1
    }
}

$composeCmd = $null
if ((docker compose version 2>$null) -and $LASTEXITCODE -eq 0) { $composeCmd = 'docker compose' }
elseif (Get-Command docker-compose -ErrorAction SilentlyContinue)  { $composeCmd = 'docker-compose' }
else {
    Write-Err "Neither 'docker compose' (plugin) nor 'docker-compose' (legacy) is available."
    exit 1
}
Write-Ok "Using compose command: $composeCmd"

# --------------------------------------------------------------------------
# 2b. Docker Desktop memory sanity check
# --------------------------------------------------------------------------
# The sandbox container requests up to 8 GB RAM (see DockerShellTool). That
# request is only honoured if Docker Desktop's VM has that much allocated
# AND the host has enough free memory to satisfy it. When the VM is
# undersized the cgroup cap is effectively the VM size, and any spike from
# az CLI / buildx / bicep parsing trips the OOM killer � reported by azd
# as the infamous "AzureCLICredential: signal: killed". Preflight here so
# the user spots the mismatch BEFORE queueing an hour-long deploy that
# will crash 2 minutes in.
$memInfo = Invoke-WithTimeout -Script {
    # 'docker info' prints 'Total Memory: 7.785GiB' among other fields.
    # Parse it without jq so we don't depend on extra tools.
    (docker info --format '{{.MemTotal}}' 2>&1)
} -Seconds 6
if ($memInfo.ok -and $memInfo.out -match '^\d+$') {
    $gib = [math]::Round([long]$memInfo.out / 1GB, 1)
    if ($gib -lt 8) {
        Write-Warn "Docker Desktop VM has only $gib GiB of RAM allocated."
        Write-Warn "The deploy sandbox requests up to 8 GiB for 'az'+'azd'+buildx;"
        Write-Warn "deploys of rich multi-service templates (e.g. azure-ai-travel-agents)"
        Write-Warn "will hit 'AzureCLICredential: signal: killed' OOM errors."
        Write-Warn ""
        Write-Warn "Fix: Docker Desktop -> Settings -> Resources -> Memory"
        Write-Warn "     Raise to at least 12 GiB (16 GiB recommended) and Apply+Restart."
        Write-Warn ""
        Write-Warn "Continuing anyway � small repos may still deploy fine."
    }
    elseif ($gib -lt 12) {
        Write-Info "Docker Desktop VM: $gib GiB RAM (OK for most deploys; 12+ GiB recommended for heavy multi-service templates)."
    }
    else {
        Write-Ok "Docker Desktop VM: $gib GiB RAM (plenty for deploy sandboxes)."
    }
}
else {
    Write-Info "Could not read Docker Desktop memory allocation (non-fatal)."
}

# --------------------------------------------------------------------------
# 3. Azure CLI + login
# --------------------------------------------------------------------------

Write-Section 'Azure CLI'

$azCli = Get-Command az -ErrorAction SilentlyContinue
if (-not $azCli) {
    Write-Warn "'az' CLI not installed on the host. The app will still run, but the in-sandbox flow will ask for a device code at deploy time. Install from https://aka.ms/installazurecliwindows for the best UX."
}
else {
    Write-Ok "az CLI found ($($azCli.Source))."

    $acc = az account show --only-show-errors 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    if ($acc -and $acc.id) {
        Write-Ok "Logged in as $($acc.user.name) (subscription: $($acc.name))."
    }
    elseif ($NonInteractive) {
        Write-Err "No Azure login detected and -NonInteractive was passed. Run 'az login' first."
        exit 1
    }
    else {
        Write-Warn "No Azure login detected. Running 'az login' (browser tab will open)..."
        az login --only-show-errors | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Err "az login failed."; exit 1 }
        Write-Ok "Login completed."
    }
}

# --------------------------------------------------------------------------
# 4. .env bootstrap
# --------------------------------------------------------------------------

Write-Section 'Configuration (.env)'

$envFile = Join-Path $repoRoot '.env'
if (-not (Test-Path $envFile)) {
    if ($NonInteractive) {
        Write-Err ".env missing and -NonInteractive was passed. Copy .env.example to .env and fill it in."
        exit 1
    }

    Write-Warn ".env not found. Let's create it."
    $endpoint = Read-Host "  Azure OpenAI endpoint (e.g. https://mycog.openai.azure.com/)"
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        Write-Err "Endpoint is required."; exit 1
    }

    $deployment = Read-Host "  Main deployment (leave blank for 'gpt-5.4')"
    if ([string]::IsNullOrWhiteSpace($deployment)) { $deployment = 'gpt-5.4' }

    $runnerDeployment = Read-Host "  Runner deployment (leave blank for 'gpt-5.3-chat')"
    if ([string]::IsNullOrWhiteSpace($runnerDeployment)) { $runnerDeployment = 'gpt-5.3-chat' }

    # Optional API key. Strongly recommended on Windows hosts: MSAL token
    # caches on Windows are DPAPI-encrypted and cannot be decrypted inside
    # a Linux container, so the copied 'az login' session can authenticate
    # to Azure ARM but NOT fetch tokens for arbitrary resources like
    # cognitiveservices.azure.com. Without an API key every Azure OpenAI
    # call will fail with a DefaultAzureCredential chain error.
    Write-Host ""
    Write-Host "  Azure OpenAI auth mode:" -ForegroundColor DarkCyan
    Write-Host "    a) API key (recommended on Windows hosts)"
    Write-Host "    b) AAD / 'az login' (works on Linux/macOS hosts; brittle on Windows)"
    $apiKey = Read-Host "  Paste an Azure OpenAI API key (press Enter to skip and use AAD)"
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        $apiKey = $null
        Write-Info "No API key provided. Falling back to DefaultAzureCredential chain (may prompt device-code flow later)."
    }

    $lines = @(
        "# Auto-generated by start.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss').",
        "# Safe to edit by hand; re-running start.ps1 leaves existing values alone.",
        "AZURE_OPENAI_ENDPOINT=$endpoint",
        "AZURE_OPENAI_DEPLOYMENT=$deployment",
        "AZURE_OPENAI_RUNNER_DEPLOYMENT=$runnerDeployment"
    )
    if ($apiKey) { $lines += "AZURE_OPENAI_API_KEY=$apiKey" }
    $lines -join "`n" | Set-Content -Path $envFile -Encoding UTF8

    Write-Ok ".env created."
}
else {
    Write-Ok ".env already exists (not modified)."
}

# --------------------------------------------------------------------------
# 5. Compose up
# --------------------------------------------------------------------------

Write-Section 'Stack'

if ($Reset) {
    $downArgs = if ($Purge) { '--volumes' } else { '' }
    Write-Info "Tearing down existing stack ($composeCmd down $downArgs)..."
    Invoke-Expression "$composeCmd down $downArgs" | Out-Null
}

if (-not $NoBuild) {
    # Always rebuild, and unless explicitly skipped also re-pull the
    # FROM layers so upstream patches of dotnet/sdk, dotnet/aspnet,
    # docker-ce-cli, az-cli land every run � not just on first build.
    # Without '--pull' a cached base image can stay weeks behind even
    # when 'docker compose up --build' is issued; the only trigger
    # would be a manual 'docker pull'. That's surprising for an
    # "always-latest" development workflow.
    $pullFlag = if ($NoPull) { '' } else { '--pull' }
    Write-Info "Building image (refreshing base layers: $([bool](-not $NoPull)))..."
    Invoke-Expression "$composeCmd build $pullFlag"
    if ($LASTEXITCODE -ne 0) {
        Write-Err "compose build failed (exit $LASTEXITCODE). Inspect the output above."
        exit 1
    }
}

# 'up -d' brings the freshly-built image online. No '--build' needed here
# since we already built above (or -NoBuild was requested).
# '--force-recreate' guarantees the running container is swapped for one
# based on the image we just (re)built. Without it, compose occasionally
# decides the existing container is "up-to-date" when the image tag stays
# 'agentichub/app:latest' and silently keeps the OLD container running,
# defeating the whole 'always latest' point of --pull + build above.
# '--remove-orphans' sweeps away any stale services from previous compose
# files (safe; we only declare one service).
Write-Info "Starting stack in detached mode ($composeCmd up -d --force-recreate --remove-orphans)..."
Invoke-Expression "$composeCmd up -d --force-recreate --remove-orphans"
if ($LASTEXITCODE -ne 0) {
    Write-Err "compose up failed (exit $LASTEXITCODE). Inspect the output above."
    exit 1
}
Write-Ok "Container started."

# Show which sandbox image the app will use for deploys. This is NOT
# pulled by compose � it's built on-demand by SandboxImageBuilder the
# first time a deploy runs against an architecture that needs it (e.g.
# arm64 swaps the amd64-only azure-dev-cli-apps image for a local
# multi-arch build tagged agentichub/sandbox:vN). Surface the tag so
# users know which revision will handle their next deploy.
$sandboxTag = docker images --format '{{.Repository}}:{{.Tag}}' | Where-Object { $_ -like 'agentichub/sandbox:*' } | Select-Object -First 1
if ($sandboxTag) {
    Write-Info "Sandbox image in cache: $sandboxTag (rebuilt automatically when the tag bumps)."
} else {
    Write-Info "Sandbox image: not built yet � will be produced on first deploy."
}

# --------------------------------------------------------------------------
# 6. Readiness probe + open browser
# --------------------------------------------------------------------------

Write-Section 'Readiness'

$url = "http://localhost:$Port/hub"
Write-Info "Polling $url (up to 120 s)..."
$ready = $false
for ($i = 1; $i -le 40; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}

if (-not $ready) {
    Write-Warn "App is not answering on $url yet. Check container logs: $composeCmd logs --tail 100"
    exit 2
}
Write-Ok "App is up at $url"

if (-not $NoBrowser) {
    if     ($isWindowsHost) { Start-Process $url }
    elseif ($IsMacOS)       { & open $url }
    else                    { & xdg-open $url 2>$null }
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  *  Tail the live log :  $composeCmd logs -f agentichub"
Write-Host "  *  Stop the stack    :  $composeCmd stop"
Write-Host "  *  Full teardown     :  pwsh .\start.ps1 -Reset"
Write-Host "  *  Wipe memory state :  pwsh .\start.ps1 -Reset -Purge"
Write-Host ""