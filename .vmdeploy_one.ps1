param([Parameter(Mandatory)][string]$LocalPath, [Parameter(Mandatory)][string]$RelPath)
$ErrorActionPreference = 'Stop'
$b = [Convert]::ToBase64String([IO.File]::ReadAllBytes($LocalPath))
$lines = @(
  '#!/bin/bash',
  'cd /home/azureuser/agentichub || exit 2',
  ('mkdir -p "$(dirname ' + $RelPath + ')" 2>/dev/null || true'),
  "echo '$b' | base64 -d > $RelPath.new",
  "mv $RelPath.new $RelPath",
  "md5sum $RelPath"
)
$tmp = [IO.Path]::GetTempFileName() + '.sh'
[IO.File]::WriteAllText($tmp, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Uploading $RelPath ($((Get-Item $tmp).Length) bytes script)..."
az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "@$tmp" --query "value[0].message" -o tsv
Remove-Item $tmp -ErrorAction SilentlyContinue
