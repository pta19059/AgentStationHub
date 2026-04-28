$bash = @'
set +e
# Find the latest sandbox workspace for investment-analysis
WS=$(ls -td /tmp/agentic-* 2>/dev/null | head -1)
echo "ws=$WS"
[ -n "$WS" ] && find "$WS" -maxdepth 4 -name "main.parameters.json" -o -name "main.bicep" 2>/dev/null
echo '--- list infra/ ---'
[ -n "$WS" ] && ls -la "$WS/infra/" 2>&1 | head -20
echo '--- list infra/bicep/ ---'
[ -n "$WS" ] && ls -la "$WS/infra/bicep/" 2>&1 | head -20
'@
$tmp = "$env:TEMP\vm-probe.sh"
Set-Content -Path $tmp -Value $bash -Encoding ascii -NoNewline
az vm run-command invoke -g rg-agentichub-host -n agentichub-host --command-id RunShellScript --scripts "@$tmp" --query 'value[0].message' -o tsv
