$ErrorActionPreference = 'Stop'

$f1 = 'c:\Work\AgentStationHub\AgentStationHub\Services\DeploymentOrchestrator.cs'
$rel1 = 'AgentStationHub/Services/DeploymentOrchestrator.cs'

# Compress to reduce script size (raw b64 is ~415KB, exceeds 256KB VM run-command limit)
$raw = [IO.File]::ReadAllBytes($f1)
$ms = [IO.MemoryStream]::new()
$gz = [IO.Compression.GZipStream]::new($ms, [IO.Compression.CompressionMode]::Compress)
$gz.Write($raw, 0, $raw.Length)
$gz.Close()
$compressed = $ms.ToArray()
$b1 = [Convert]::ToBase64String($compressed)
Write-Host "Original: $($raw.Length) bytes, Compressed+B64: $($b1.Length) chars"

$lines = @(
  '#!/bin/bash'
  'set -eu'
  'cd /home/azureuser/agentichub'
  "echo $b1 | base64 -d | gunzip > $rel1"
  "wc -c $rel1"
  'echo === Rebuilding ==='
  'sudo -u azureuser docker compose build agentichub 2>&1 | tail -5'
  'echo === Restarting ==='
  'sudo -u azureuser docker compose up -d --force-recreate --no-deps agentichub 2>&1 | tail -4'
  'sleep 6'
  "sudo -u azureuser docker ps --filter name=agentichub-app --format '{{.Status}}'"
  'echo === DONE ==='
)
$tmp = "$env:TEMP\vmdeploy_gz.sh"
[IO.File]::WriteAllText($tmp, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Script: $((Get-Item $tmp).Length) bytes"

az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "@$tmp" --query "value[0].message" -o tsv
Remove-Item $tmp -ErrorAction SilentlyContinue
