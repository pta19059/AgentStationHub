<#
.SYNOPSIS
    Start the AgentStationHub stack on a remote Azure VM and open a
    browser tab pointing at it.

.DESCRIPTION
    Local Docker Desktop saturates the user's laptop when sandbox
    deployments fan out (8+ services x multi-stage builds). This script
    drives a dedicated Azure VM that runs the whole 'docker compose'
    stack server-side. Local PC does NOTHING heavy.

    The script is IDEMPOTENT: rerun it any time. It will:

      1. Verify SSH key + 'az' CLI are present locally.
      2. Ensure the VM exists in resource group rg-agentichub-host
         (creates it on first run via the bootstrap path - see -Bootstrap).
      3. If the VM is deallocated, start it and wait for SSH.
      4. Sync the local repo + .env + ~/.azure profile to the VM.
      5. Run 'docker compose up -d --force-recreate' on the VM.
      6. Probe http://<vm-ip>:8080/ and open the browser when ready.

    To shut everything down (deallocate VM = stop billing) use stop.ps1.

    For the legacy LOCAL Docker flow (laptop runs the stack) use
    'pwsh .\start-local.ps1'.

.EXAMPLE
    pwsh .\start.ps1
        Start (or resume) the remote VM and open the browser.

.EXAMPLE
    pwsh .\start.ps1 -SkipSync
        Skip repo/env/profile sync (fastest restart).

.EXAMPLE
    pwsh .\start.ps1 -SkipBuild
        Skip 'docker compose build', just 'up -d'.

.EXAMPLE
    pwsh .\start.ps1 -SshOnly
        Just open an interactive SSH session to the VM.

.EXAMPLE
    pwsh .\start.ps1 -Bootstrap
        First-time provisioning: creates RG + VM + NSG rules + SSH key.
#>

[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-agentichub-host',
    [string] $VmName        = 'agentichub-host',
    [string] $Location      = 'eastus2',
    [string] $VmSize        = 'Standard_D2as_v6',
    [string] $AdminUser     = 'azureuser',
    [string] $SshKey        = "$env:USERPROFILE\.ssh\agentichub-host",
    [switch] $SkipSync,
    [switch] $SkipBuild,
    [switch] $NoBrowser,
    [switch] $SshOnly,
    [switch] $Bootstrap
)

$ErrorActionPreference = 'Stop'

function Write-Hr ($title) {
    Write-Host ""
    Write-Host ("=== {0} {1}" -f $title, ('=' * [Math]::Max(0, 70 - $title.Length))) -ForegroundColor Cyan
}
function Write-Ok ($msg)    { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Info ($msg)  { Write-Host "   *   $msg" -ForegroundColor Gray }
function Write-Warn ($msg)  { Write-Host "  [!]  $msg" -ForegroundColor Yellow }
function Write-Err ($msg)   { Write-Host "  [X]  $msg" -ForegroundColor Red }

# --- Prerequisites ----------------------------------------------------------

Write-Hr 'Prerequisites'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Err "'az' CLI not on PATH. Install from https://aka.ms/installazurecliwindows"
    exit 1
}
Write-Ok "az CLI found."

if (-not (Test-Path $SshKey) -and -not $Bootstrap) {
    Write-Err "SSH private key not found: $SshKey"
    Write-Info "Run with -Bootstrap to generate one and provision the VM from scratch."
    exit 1
}

$sshOpts = @(
    '-i', $SshKey,
    '-o', 'StrictHostKeyChecking=no',
    '-o', 'UserKnownHostsFile=/dev/null',
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=10'
)

# --- Subscription -----------------------------------------------------------

Write-Hr 'Azure'
$sub = az account show --query id -o tsv 2>$null
if (-not $sub) {
    Write-Warn "Not logged in. Running 'az login'..."
    az login --only-show-errors | Out-Null
    $sub = az account show --query id -o tsv
}
$user = az account show --query user.name -o tsv
Write-Ok "Subscription: $sub"
Write-Ok "Logged in as: $user"

# --- Bootstrap (first-time provisioning) ------------------------------------

if ($Bootstrap) {
    Write-Hr 'Bootstrap (first-time VM provisioning)'

    if (-not (az group show -n $ResourceGroup --query name -o tsv 2>$null)) {
        Write-Info "Creating resource group $ResourceGroup in $Location..."
        az group create -n $ResourceGroup -l $Location --query 'properties.provisioningState' -o tsv | Out-Null
        Write-Ok "Resource group created."
    }

    if (-not (Test-Path $SshKey)) {
        Write-Info "Generating SSH key at $SshKey..."
        $sshDir = Split-Path $SshKey -Parent
        if (-not (Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir | Out-Null }
        ssh-keygen -t ed25519 -f $SshKey -N '' -C 'agentichub-host' | Out-Null
        Write-Ok "Key generated."
    }

    $existing = az vm show -g $ResourceGroup -n $VmName --query name -o tsv 2>$null
    if (-not $existing) {
        Write-Info "Creating VM $VmName ($VmSize, Ubuntu 22.04). This takes ~2 min..."
        az vm create `
            --resource-group $ResourceGroup `
            --name $VmName `
            --image Ubuntu2204 `
            --size $VmSize `
            --admin-username $AdminUser `
            --ssh-key-values "$SshKey.pub" `
            --public-ip-sku Standard `
            --os-disk-size-gb 64 `
            --nsg-rule NONE `
            --assign-identity `
            --tags purpose=agentichub-host `
            --query 'powerState' -o tsv | Out-Null
        Write-Ok "VM created."
    }

    $myIp = (Invoke-RestMethod 'https://api.ipify.org').Trim()
    Write-Info "Configuring NSG to allow SSH + 8080 from $myIp/32..."
    az network nsg rule create -g $ResourceGroup --nsg-name "${VmName}NSG" -n AllowSSHFromMyIP `
        --priority 1000 --direction Inbound --access Allow --protocol Tcp `
        --source-address-prefixes "$myIp/32" --destination-port-ranges 22 `
        --query 'provisioningState' -o tsv 2>$null | Out-Null
    az network nsg rule create -g $ResourceGroup --nsg-name "${VmName}NSG" -n AllowAppFromMyIP `
        --priority 1010 --direction Inbound --access Allow --protocol Tcp `
        --source-address-prefixes "$myIp/32" --destination-port-ranges 8080 `
        --query 'provisioningState' -o tsv 2>$null | Out-Null
    Write-Ok "NSG rules in place."

    Write-Info "Bootstrapping Docker + Azure CLI on VM..."
    $ip = az vm show -g $ResourceGroup -n $VmName -d --query publicIps -o tsv
    Start-Sleep -Seconds 15
    $bootstrap = @'
set -eux
sudo apt-get update -y
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl gnupg jq rsync
sudo install -m 0755 -d /etc/apt/keyrings
if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  sudo chmod a+r /etc/apt/keyrings/docker.gpg
fi
. /etc/os-release
echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update -y
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker azureuser
sudo systemctl enable --now docker
if ! command -v az >/dev/null; then curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash; fi
sudo mkdir -p /var/agentichub-work /var/agentichub-tools
sudo chown -R azureuser:azureuser /var/agentichub-work /var/agentichub-tools
mkdir -p /home/azureuser/agentichub /home/azureuser/.azure
echo BOOTSTRAP_OK
'@
    $bootstrap | & ssh @sshOpts "$AdminUser@$ip" 'bash -s' 2>&1 | Select-Object -Last 5
    Write-Ok "Bootstrap done."
}

# --- VM state ---------------------------------------------------------------

Write-Hr 'Virtual machine'
$vm = az vm show -g $ResourceGroup -n $VmName --query '{name:name,location:location}' -o json 2>$null | ConvertFrom-Json
if (-not $vm) {
    Write-Err "VM '$VmName' not found in '$ResourceGroup'. Run with -Bootstrap to create it."
    exit 1
}
Write-Ok "VM: $($vm.name) ($($vm.location))"

$power = az vm get-instance-view -g $ResourceGroup -n $VmName --query "instanceView.statuses[?starts_with(code,'PowerState/')].displayStatus | [0]" -o tsv
Write-Info "Power state: $power"
if ($power -ne 'VM running') {
    Write-Info "Starting VM..."
    az vm start -g $ResourceGroup -n $VmName | Out-Null
    Write-Ok "VM started."
}

$ip = az vm show -g $ResourceGroup -n $VmName -d --query publicIps -o tsv
Write-Ok "Public IP: $ip"

# Refresh NSG allow-rule with current public IP (it changes on most ISPs).
$myIp = (Invoke-RestMethod 'https://api.ipify.org').Trim()
Write-Info "Refreshing NSG allow-list for current public IP $myIp..."
az network nsg rule update -g $ResourceGroup --nsg-name "${VmName}NSG" -n AllowSSHFromMyIP `
    --source-address-prefixes "$myIp/32" --query 'provisioningState' -o tsv 2>$null | Out-Null
az network nsg rule update -g $ResourceGroup --nsg-name "${VmName}NSG" -n AllowAppFromMyIP `
    --source-address-prefixes "$myIp/32" --query 'provisioningState' -o tsv 2>$null | Out-Null

# --- Wait for SSH -----------------------------------------------------------

Write-Hr 'SSH'
Write-Info "Waiting for SSH on $ip..."
$sshOk = $false
for ($i = 0; $i -lt 30; $i++) {
    $r = & ssh @sshOpts "$AdminUser@$ip" 'echo READY' 2>$null
    if ($r -match 'READY') { $sshOk = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $sshOk) {
    Write-Err "SSH did not become available within 150s."
    exit 1
}
Write-Ok "SSH ready."

if ($SshOnly) {
    Write-Info "Opening interactive SSH session..."
    & ssh -i $SshKey "$AdminUser@$ip"
    return
}

# --- Sync repo + .env + .azure ----------------------------------------------

if (-not $SkipSync) {
    Write-Hr 'Sync'

    $repoRoot = $PSScriptRoot
    Write-Info "Syncing repo from $repoRoot ..."
    Push-Location $repoRoot
    try {
        # Hard-exclude .env and appsettings.Development.json from the repo
        # archive: those files live ONLY on the VM (rotated independently of
        # source) and a stray copy in the local checkout would otherwise
        # silently overwrite the production secrets via this sync.
        tar -cf - `
            --exclude='*/bin/*' --exclude='*/obj/*' --exclude='.git' `
            --exclude='*/node_modules/*' --exclude='*.user' `
            --exclude='azure-readonly' --exclude='*.log' `
            --exclude='./.env' --exclude='**/appsettings.Development.json' . `
            | & ssh @sshOpts "$AdminUser@$ip" 'mkdir -p /home/azureuser/agentichub && cd /home/azureuser/agentichub && tar -xf -' `
            | Out-Null
    } finally { Pop-Location }
    Write-Ok "Repo synced."

    $dotenv = Join-Path $repoRoot '.env'
    if (Test-Path $dotenv) {
        & scp @sshOpts $dotenv "${AdminUser}@${ip}:/home/azureuser/agentichub/.env" | Out-Null
        Write-Ok ".env synced."
    } else {
        Write-Warn ".env not found locally; remote .env (if any) is left in place."
    }

    $azDir = "$env:USERPROFILE\.azure"
    if (Test-Path $azDir) {
        Write-Info "Syncing Azure CLI profile (root files only)..."
        Push-Location $azDir
        try {
            tar -cf - --exclude='cliextensions' --exclude='cliextensions_clean' `
                --exclude='bin' --exclude='logs' --exclude='telemetry' --exclude='*.log' . `
                | & ssh @sshOpts "$AdminUser@$ip" 'mkdir -p /home/azureuser/.azure && cd /home/azureuser/.azure && tar -xf -' `
                | Out-Null
        } finally { Pop-Location }
        Write-Ok "Azure profile synced."
    }
}

# --- Compose up -------------------------------------------------------------

Write-Hr 'Stack'

$buildPart = if ($SkipBuild) { 'echo "skipping build";' } else { 'sudo docker compose build agentichub 2>&1 | tail -8;' }
$composeCmd = "set -e; cd /home/azureuser/agentichub; $buildPart sudo docker compose up -d --force-recreate 2>&1 | tail -8; sleep 5; sudo docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"
& ssh @sshOpts "$AdminUser@$ip" $composeCmd

# --- Readiness probe --------------------------------------------------------

Write-Hr 'Readiness'
$url = "http://${ip}:8080/"
Write-Info "Polling $url (up to 120s)..."
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    try {
        $r = Invoke-WebRequest $url -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        if ($r.StatusCode -in 200, 302) { $ready = $true; break }
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -in 200, 302, 404) { $ready = $true; break }
    }
    Start-Sleep -Seconds 3
}

if ($ready) {
    Write-Ok "App is up at $url"
} else {
    Write-Err "App did not respond in 120s. Recent logs:"
    & ssh @sshOpts "$AdminUser@$ip" 'sudo docker logs --tail 50 agentichub-app 2>&1'
    exit 1
}

# --- Browser ----------------------------------------------------------------
#
# Copilot CLI is reachable through the Hub itself at $url/copilot/ (the
# Hub reverse-proxies /copilot/ to the ttyd sidecar on the compose docker
# network). No SSH tunnel, no extra port, no NSG hole.
if (-not $NoBrowser) { Start-Process $url }

# --- Wrap-up ----------------------------------------------------------------

Write-Hr 'Done'
Write-Host ""
Write-Host "  App URL       : $url"
Write-Host "  Copilot CLI   : ${url}copilot/  (proxied through the Hub)"
Write-Host "  SSH           : ssh -i `"$SshKey`" $AdminUser@$ip"
Write-Host "  Tail logs     : ssh -i `"$SshKey`" $AdminUser@$ip 'sudo docker logs -f agentichub-app'"
Write-Host "  Stop VM       : pwsh .\stop.ps1     (deallocates -> stops billing)"
Write-Host "  Resume        : pwsh .\start.ps1"
Write-Host ""
