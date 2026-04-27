#!/usr/bin/env bash
# ----------------------------------------------------------------------------
# AgentStationHub - REMOTE start (Azure VM).
#
# Equivalent of start.ps1 for macOS / Linux laptops. Drives a dedicated
# Azure VM that runs the whole `docker compose` stack server-side so the
# local laptop is not loaded by sandbox container fan-out.
#
# Idempotent: rerun anytime. To stop billing, run ./stop.sh
# To use the legacy LOCAL Docker flow, run ./start-local.sh
#
# Flags:
#   --skip-sync       Skip repo / .env / .azure sync.
#   --skip-build      Skip 'docker compose build'.
#   --no-browser      Do not auto-open the browser.
#   --ssh-only        Just open an interactive SSH session.
#   --bootstrap       First-time: create RG + VM + NSG + bootstrap.
# ----------------------------------------------------------------------------
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-agentichub-host}"
VM_NAME="${VM_NAME:-agentichub-host}"
LOCATION="${LOCATION:-eastus2}"
VM_SIZE="${VM_SIZE:-Standard_D2as_v6}"
ADMIN_USER="${ADMIN_USER:-azureuser}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/agentichub-host}"

SKIP_SYNC=0; SKIP_BUILD=0; NO_BROWSER=0; SSH_ONLY=0; BOOTSTRAP=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-sync)  SKIP_SYNC=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    --no-browser) NO_BROWSER=1 ;;
    --ssh-only)   SSH_ONLY=1 ;;
    --bootstrap)  BOOTSTRAP=1 ;;
    -h|--help)    sed -n '1,20p' "$0"; exit 0 ;;
    *) echo "Unknown flag: $1" >&2; exit 2 ;;
  esac
  shift
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

ok()   { printf "  \033[32m[OK]\033[0m %s\n" "$1"; }
info() { printf "  \033[90m*\033[0m   %s\n"  "$1"; }
warn() { printf "  \033[33m[!]\033[0m  %s\n" "$1"; }
err()  { printf "  \033[31m[X]\033[0m  %s\n" "$1" >&2; }
hr()   { printf "\n\033[36m=== %s \033[0m%s\n" "$1" "$(printf '=%.0s' $(seq $((70 - ${#1}))))"; }

SSH_OPTS=(-i "$SSH_KEY" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=10)

# --- Prerequisites ----------------------------------------------------------

hr "Prerequisites"
command -v az  >/dev/null || { err "az CLI not on PATH (https://aka.ms/installazurecli)"; exit 1; }
command -v ssh >/dev/null || { err "ssh client not on PATH"; exit 1; }
command -v tar >/dev/null || { err "tar not on PATH"; exit 1; }
ok "az + ssh + tar present."

if [[ ! -f "$SSH_KEY" && $BOOTSTRAP -eq 0 ]]; then
  err "SSH key not found: $SSH_KEY"
  info "Run with --bootstrap to generate one and provision the VM."
  exit 1
fi

# --- Subscription -----------------------------------------------------------

hr "Azure"
if ! az account show >/dev/null 2>&1; then
  warn "Not logged in. Running 'az login'..."
  az login --only-show-errors >/dev/null
fi
SUB="$(az account show --query id -o tsv)"
USER_NAME="$(az account show --query user.name -o tsv)"
ok "Subscription: $SUB"
ok "Logged in as: $USER_NAME"

# --- Bootstrap (first-time provisioning) ------------------------------------

if [[ $BOOTSTRAP -eq 1 ]]; then
  hr "Bootstrap"

  if ! az group show -n "$RESOURCE_GROUP" --query name -o tsv >/dev/null 2>&1; then
    info "Creating resource group $RESOURCE_GROUP in $LOCATION..."
    az group create -n "$RESOURCE_GROUP" -l "$LOCATION" --query 'properties.provisioningState' -o tsv >/dev/null
    ok "Resource group created."
  fi

  if [[ ! -f "$SSH_KEY" ]]; then
    info "Generating SSH key at $SSH_KEY..."
    mkdir -p "$(dirname "$SSH_KEY")"
    ssh-keygen -t ed25519 -f "$SSH_KEY" -N '' -C 'agentichub-host' >/dev/null
    ok "Key generated."
  fi

  if ! az vm show -g "$RESOURCE_GROUP" -n "$VM_NAME" --query name -o tsv >/dev/null 2>&1; then
    info "Creating VM $VM_NAME ($VM_SIZE, Ubuntu 22.04)..."
    az vm create \
      --resource-group "$RESOURCE_GROUP" \
      --name "$VM_NAME" \
      --image Ubuntu2204 \
      --size "$VM_SIZE" \
      --admin-username "$ADMIN_USER" \
      --ssh-key-values "$SSH_KEY.pub" \
      --public-ip-sku Standard \
      --os-disk-size-gb 64 \
      --nsg-rule NONE \
      --assign-identity \
      --tags purpose=agentichub-host \
      --query 'powerState' -o tsv >/dev/null
    ok "VM created."
  fi

  MY_IP="$(curl -fsSL https://api.ipify.org)"
  info "Configuring NSG to allow SSH + 8080 from $MY_IP/32..."
  az network nsg rule create -g "$RESOURCE_GROUP" --nsg-name "${VM_NAME}NSG" -n AllowSSHFromMyIP \
    --priority 1000 --direction Inbound --access Allow --protocol Tcp \
    --source-address-prefixes "$MY_IP/32" --destination-port-ranges 22 \
    --query 'provisioningState' -o tsv >/dev/null 2>&1 || true
  az network nsg rule create -g "$RESOURCE_GROUP" --nsg-name "${VM_NAME}NSG" -n AllowAppFromMyIP \
    --priority 1010 --direction Inbound --access Allow --protocol Tcp \
    --source-address-prefixes "$MY_IP/32" --destination-port-ranges 8080 \
    --query 'provisioningState' -o tsv >/dev/null 2>&1 || true
  ok "NSG rules in place."

  info "Bootstrapping Docker + az CLI on VM (~3 min)..."
  IP="$(az vm show -g "$RESOURCE_GROUP" -n "$VM_NAME" -d --query publicIps -o tsv)"
  sleep 15
  ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'bash -s' <<'BOOT'
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
BOOT
  ok "Bootstrap done."
fi

# --- VM state ---------------------------------------------------------------

hr "Virtual machine"
if ! az vm show -g "$RESOURCE_GROUP" -n "$VM_NAME" --query name -o tsv >/dev/null 2>&1; then
  err "VM '$VM_NAME' not found in '$RESOURCE_GROUP'. Run with --bootstrap to create it."
  exit 1
fi
ok "VM: $VM_NAME ($LOCATION)"

POWER="$(az vm get-instance-view -g "$RESOURCE_GROUP" -n "$VM_NAME" --query "instanceView.statuses[?starts_with(code,'PowerState/')].displayStatus | [0]" -o tsv)"
info "Power state: $POWER"
if [[ "$POWER" != "VM running" ]]; then
  info "Starting VM..."
  az vm start -g "$RESOURCE_GROUP" -n "$VM_NAME" >/dev/null
  ok "VM started."
fi

IP="$(az vm show -g "$RESOURCE_GROUP" -n "$VM_NAME" -d --query publicIps -o tsv)"
ok "Public IP: $IP"

MY_IP="$(curl -fsSL https://api.ipify.org)"
info "Refreshing NSG allow-list for current public IP $MY_IP..."
az network nsg rule update -g "$RESOURCE_GROUP" --nsg-name "${VM_NAME}NSG" -n AllowSSHFromMyIP \
  --source-address-prefixes "$MY_IP/32" --query 'provisioningState' -o tsv >/dev/null 2>&1 || true
az network nsg rule update -g "$RESOURCE_GROUP" --nsg-name "${VM_NAME}NSG" -n AllowAppFromMyIP \
  --source-address-prefixes "$MY_IP/32" --query 'provisioningState' -o tsv >/dev/null 2>&1 || true

# --- Wait for SSH -----------------------------------------------------------

hr "SSH"
info "Waiting for SSH on $IP..."
SSH_OK=0
for i in $(seq 1 30); do
  if ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'echo READY' 2>/dev/null | grep -q READY; then
    SSH_OK=1; break
  fi
  sleep 5
done
[[ $SSH_OK -eq 1 ]] || { err "SSH did not come up in 150s."; exit 1; }
ok "SSH ready."

if [[ $SSH_ONLY -eq 1 ]]; then
  info "Opening interactive SSH..."
  exec ssh -i "$SSH_KEY" "$ADMIN_USER@$IP"
fi

# --- Sync repo + .env + .azure ----------------------------------------------

if [[ $SKIP_SYNC -eq 0 ]]; then
  hr "Sync"

  info "Syncing repo from $SCRIPT_DIR ..."
  ( cd "$SCRIPT_DIR" && tar -cf - \
      --exclude='*/bin/*' --exclude='*/obj/*' --exclude='.git' \
      --exclude='*/node_modules/*' --exclude='*.user' \
      --exclude='azure-readonly' --exclude='*.log' . \
    | ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'mkdir -p /home/azureuser/agentichub && cd /home/azureuser/agentichub && tar -xf -' )
  ok "Repo synced."

  if [[ -f "$SCRIPT_DIR/.env" ]]; then
    scp "${SSH_OPTS[@]}" "$SCRIPT_DIR/.env" "$ADMIN_USER@$IP:/home/azureuser/agentichub/.env" >/dev/null
    ok ".env synced."
  else
    warn ".env not found locally; remote .env (if any) is left in place."
  fi

  if [[ -d "$HOME/.azure" ]]; then
    info "Syncing Azure CLI profile (root files only)..."
    ( cd "$HOME/.azure" && tar -cf - \
        --exclude='cliextensions' --exclude='cliextensions_clean' \
        --exclude='bin' --exclude='logs' --exclude='telemetry' --exclude='*.log' . \
      | ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'mkdir -p /home/azureuser/.azure && cd /home/azureuser/.azure && tar -xf -' )
    ok "Azure profile synced."
  fi
fi

# --- Compose up -------------------------------------------------------------

hr "Stack"
BUILD_PART='sudo docker compose build agentichub 2>&1 | tail -8;'
[[ $SKIP_BUILD -eq 1 ]] && BUILD_PART='echo "skipping build";'
ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" "set -e; cd /home/azureuser/agentichub; $BUILD_PART sudo docker compose up -d --force-recreate 2>&1 | tail -8; sleep 5; sudo docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"

# --- Readiness probe --------------------------------------------------------

hr "Readiness"
URL="http://$IP:8080/"
info "Polling $URL (up to 120s)..."
READY=0
for i in $(seq 1 40); do
  if curl -fsS --max-time 5 "$URL" -o /dev/null 2>&1; then READY=1; break; fi
  # 404 also counts as 'app responding'
  CODE="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$URL" || echo 000)"
  if [[ "$CODE" =~ ^(200|302|404)$ ]]; then READY=1; break; fi
  sleep 3
done

if [[ $READY -eq 1 ]]; then
  ok "App is up at $URL"
else
  err "App did not respond in 120s. Recent logs:"
  ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'sudo docker logs --tail 50 agentichub-app 2>&1'
  exit 1
fi

# --- Browser ----------------------------------------------------------------
#
# Copilot CLI is reachable through the Hub itself at $URL/copilot/ (the
# Hub reverse-proxies /copilot/ to the ttyd sidecar on the compose docker
# network). No SSH tunnel, no extra port, no NSG hole.
if [[ $NO_BROWSER -eq 0 ]]; then
  if   command -v open     >/dev/null; then open "$URL"
  elif command -v xdg-open >/dev/null; then xdg-open "$URL" >/dev/null 2>&1 &
  fi
fi

# --- Wrap-up ----------------------------------------------------------------

hr "Done"
cat <<EOF

  App URL       : $URL
  Copilot CLI   : ${URL}copilot/  (proxied through the Hub)
  SSH           : ssh -i "$SSH_KEY" $ADMIN_USER@$IP
  Tail logs     : ssh -i "$SSH_KEY" $ADMIN_USER@$IP 'sudo docker logs -f agentichub-app'
  Stop VM       : ./stop.sh        (deallocates -> stops billing)
  Resume        : ./start.sh

EOF
