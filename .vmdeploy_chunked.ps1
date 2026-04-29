param(
  [Parameter(Mandatory)][string]$LocalPath,
  [Parameter(Mandatory)][string]$RelPath,
  [int]$ChunkSize = 150000
)
$ErrorActionPreference = 'Stop'
$b = [Convert]::ToBase64String([IO.File]::ReadAllBytes($LocalPath))
$total = $b.Length
$chunks = [Math]::Ceiling($total / $ChunkSize)
Write-Host "Uploading $RelPath in $chunks chunk(s) ($total b64 chars total)..."
for ($i = 0; $i -lt $chunks; $i++) {
  $start = $i * $ChunkSize
  $len = [Math]::Min($ChunkSize, $total - $start)
  $part = $b.Substring($start, $len)
  $isFirst = ($i -eq 0)
  $isLast  = ($i -eq $chunks - 1)
  $redirect = if ($isFirst) { '>' } else { '>>' }
  $tmpPath = "$RelPath.b64.part"
  $lines = @(
    '#!/bin/bash',
    'set -e',
    'cd /home/azureuser/agentichub',
    ('mkdir -p "$(dirname ' + $RelPath + ')" 2>/dev/null || true'),
    ("printf '%s' '" + $part + "' " + $redirect + " " + $tmpPath)
  )
  if ($isLast) {
    $lines += "base64 -d $tmpPath > $RelPath.new && mv $RelPath.new $RelPath && rm -f $tmpPath"
    $lines += "md5sum $RelPath"
    $lines += "wc -c $RelPath"
  } else {
    $lines += "wc -c $tmpPath"
  }
  $tmp = [IO.Path]::GetTempFileName() + '.sh'
  [IO.File]::WriteAllText($tmp, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
  Write-Host "  chunk $($i+1)/$chunks ($len b64 chars, script $((Get-Item $tmp).Length) bytes)..."
  $r = az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "@$tmp" --query "value[0].message" -o tsv
  Remove-Item $tmp -ErrorAction SilentlyContinue
  Write-Host $r
}
