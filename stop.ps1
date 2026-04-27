<#
.SYNOPSIS
    Deallocate the AgentStationHub Azure VM (stops billing for compute).

.DESCRIPTION
    Stops + deallocates the VM. The OS disk + public IP remain (small
    storage cost ~$3-5/month). Resume with .\start.ps1.

.EXAMPLE
    pwsh .\stop.ps1
        Deallocate the VM.

.EXAMPLE
    pwsh .\stop.ps1 -Destroy
        Delete the entire resource group (irreversible).
#>

[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-agentichub-host',
    [string] $VmName        = 'agentichub-host',
    [switch] $Destroy
)

$ErrorActionPreference = 'Stop'

if ($Destroy) {
    $confirm = Read-Host "DELETE resource group '$ResourceGroup' and all resources? Type 'DELETE' to confirm"
    if ($confirm -ne 'DELETE') { Write-Host "Aborted." -ForegroundColor Yellow; exit 0 }
    Write-Host "Deleting resource group $ResourceGroup..." -ForegroundColor Yellow
    az group delete -n $ResourceGroup --yes --no-wait | Out-Null
    Write-Host "[OK] Delete dispatched (running async)." -ForegroundColor Green
    return
}

Write-Host "Deallocating VM $VmName ..." -ForegroundColor Cyan
az vm deallocate -g $ResourceGroup -n $VmName | Out-Null
$power = az vm get-instance-view -g $ResourceGroup -n $VmName --query "instanceView.statuses[?starts_with(code,'PowerState/')].displayStatus | [0]" -o tsv
Write-Host "[OK] VM state: $power (no compute billing)" -ForegroundColor Green
Write-Host ""
Write-Host "  Resume   : pwsh .\start.ps1"
Write-Host "  Destroy  : pwsh .\stop.ps1 -Destroy   (deletes RG)"
Write-Host ""
