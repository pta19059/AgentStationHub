#!/bin/bash
TOK=$(curl -s -X POST -d "client_id=$AZURE_CLIENT_ID&client_secret=$AZURE_CLIENT_SECRET&scope=https://ai.azure.com/.default&grant_type=client_credentials" https://login.microsoftonline.com/$AZURE_TENANT_ID/oauth2/v2.0/token | grep -o 'access_token":"[^"]*' | cut -d'"' -f3)
VER=${1:-6}
BODY='{"command":"remediate","workspace":"/workspace","failedStepId":1,"errorTail":"echo: command not found","previousAttempts":[],"plan":{"prerequisites":[],"env":{},"steps":[{"id":1,"description":"echo hello","cmd":"echo hello","cwd":"."}],"verifyHints":[]}}'
curl -sS -m 120 -o /tmp/r.txt -w "HTTP=%{http_code} time=%{time_total}\n" \
  -X POST \
  -H "Authorization: Bearer $TOK" \
  -H "Content-Type: application/json" \
  -d "$BODY" \
  "https://agenticstationfoundry.services.ai.azure.com/api/projects/default/agents/ash-doctor-hosted/versions/$VER/invocations?api-version=v1"
echo "---response---"
head -c 4000 /tmp/r.txt
echo
