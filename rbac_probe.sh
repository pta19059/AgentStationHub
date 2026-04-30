#!/bin/bash
SUB=e5442d96-b962-4973-9cef-623903983c17
OBJ=9acd5c28-cc74-47f4-a6a2-8c049f93a7ae
echo "=== ACCOUNT (cached token) ==="
az account show -o table 2>&1 | head -20
echo
echo "=== SIGNED-IN USER ==="
az ad signed-in-user show -o json 2>&1 | head -30
echo
echo "=== ROLES on SUB scope (assignee=$OBJ) ==="
az role assignment list --assignee "$OBJ" --scope "/subscriptions/$SUB" --include-inherited --include-groups -o table 2>&1
echo
echo "=== ROLES across ALL scopes ==="
az role assignment list --assignee "$OBJ" --all --include-inherited --include-groups --query "[].{role:roleDefinitionName, scope:scope}" -o table 2>&1 | head -40
echo
echo "=== Try a no-op validate to confirm ==="
az deployment sub validate --location eastus --template-file /dev/null 2>&1 | head -10 || true
