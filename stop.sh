#!/usr/bin/env bash
# Deallocate the AgentStationHub Azure VM (stops compute billing).
# Use --destroy to delete the entire resource group (irreversible).
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-agentichub-host}"
VM_NAME="${VM_NAME:-agentichub-host}"
DESTROY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --destroy) DESTROY=1 ;;
    -h|--help) sed -n '1,4p' "$0"; exit 0 ;;
    *) echo "Unknown flag: $1" >&2; exit 2 ;;
  esac
  shift
done

if [[ $DESTROY -eq 1 ]]; then
  read -r -p "DELETE resource group '$RESOURCE_GROUP'? Type 'DELETE' to confirm: " confirm
  if [[ "$confirm" != "DELETE" ]]; then echo "Aborted."; exit 0; fi
  echo "Deleting resource group $RESOURCE_GROUP..."
  az group delete -n "$RESOURCE_GROUP" --yes --no-wait >/dev/null
  echo "[OK] Delete dispatched (running async)."
  exit 0
fi

echo "Deallocating VM $VM_NAME ..."
az vm deallocate -g "$RESOURCE_GROUP" -n "$VM_NAME" >/dev/null
POWER="$(az vm get-instance-view -g "$RESOURCE_GROUP" -n "$VM_NAME" --query "instanceView.statuses[?starts_with(code,'PowerState/')].displayStatus | [0]" -o tsv)"
echo "[OK] VM state: $POWER (no compute billing)"
echo
echo "  Resume   : ./start.sh"
echo "  Destroy  : ./stop.sh --destroy"
echo
