$ErrorActionPreference = 'Stop'
$f1 = 'c:\Work\AgentStationHub\AgentStationHub.SandboxRunner\Team\PlanningTeam.cs'
$f2 = 'c:\Work\AgentStationHub\AgentStationHub\Services\Tools\FoundryDoctorClient.cs'
$rel1 = 'AgentStationHub.SandboxRunner/Team/PlanningTeam.cs'
$rel2 = 'AgentStationHub/Services/Tools/FoundryDoctorClient.cs'
$b1 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f1))
$b2 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f2))
$lines = @(
  '#!/bin/bash',
  'set -eu',
  'cd /home/azureuser/agentichub',
  "echo '$b1' | base64 -d > $rel1",
  "echo '$b2' | base64 -d > $rel2",
  "md5sum $rel1 $rel2",
  'sudo -u azureuser docker compose build agentichub 2>&1 | tail -3',
  'sudo -u azureuser docker compose up -d --force-recreate --no-deps agentichub 2>&1 | tail -4',
  'sleep 6',
  "sudo -u azureuser docker ps --filter name=agentichub-app --format '{{.Status}}'"
)
$tmp = "$env:TEMP\vmdeploy_v2.sh"
[IO.File]::WriteAllText($tmp, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $tmp ($((Get-Item $tmp).Length) bytes)"
az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "@$tmp" --query "value[0].message" -o tsv
Remove-Item $tmp -ErrorAction SilentlyContinue
