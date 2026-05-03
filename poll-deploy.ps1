# Polling 6-min sul deploy in corso (sandbox asb-3b35dfc80a3e + agentichub-app)
# Uso: .\poll-deploy.ps1
$rg = "rg-agentichub-host"; $vm = "agentichub-host"
$bash = @"
echo '=== TS ==='; date -u
echo '=== docker ps -a ==='; docker ps -a --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}' | head -20
echo '=== app log (since 7m) ==='; docker logs --since 7m agentichub-app 2>&1 | tail -60
echo '=== sandbox log (since 7m) ==='; docker logs --since 7m asb-3b35dfc80a3e 2>&1 | tail -60
echo '=== azd state ==='; docker exec asb-3b35dfc80a3e bash -lc 'cd /workspace && azd env get-values 2>&1 | tail -20'
"@ -replace "`r`n","`n"
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bash))
$wrap = "echo $b64 | base64 -d | bash"
while ($true) {
  $t = (Get-Date).ToString("HH:mm:ss")
  Write-Host "`n========== POLL @ $t ==========" -ForegroundColor Cyan
  az vm run-command invoke -g $rg -n $vm --command-id RunShellScript --scripts $wrap --query "value[0].message" -o tsv
  Write-Host "Sleeping 360s..."
  Start-Sleep -Seconds 360
}
