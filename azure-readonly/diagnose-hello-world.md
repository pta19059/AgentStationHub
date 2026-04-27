# Diagnosi "Container rimane su hello-world" - azure-ai-travel-agents

## 1. Verifica quale immagine sta girando in ogni Container App

```bash
az containerapp list -g rg-azure-ai-travel-agents-eastus \
  --query "[].{name:name, image:properties.template.containers[0].image, revision:properties.latestRevisionName}" -o table
```

Se vedi `mcr.microsoft.com/azuredocs/containerapps-helloworld` -> il `azd deploy` non è mai andato a buon fine per quel servizio.

## 2. Forza la fase di deploy (build + push immagini + update container apps)

```bash
azd deploy --no-prompt
```

Oppure per un singolo servizio:
```bash
azd deploy ui-angular --no-prompt
azd deploy api-maf-python --no-prompt
azd deploy api-llamaindex-ts --no-prompt
azd deploy mcp-customer-query --no-prompt
azd deploy mcp-destination-recommendation --no-prompt
azd deploy mcp-itinerary-planning --no-prompt
azd deploy mcp-echo-ping --no-prompt
```

## 3. Se `azd deploy` fallisce, guarda i log build

```bash
azd deploy --debug 2>&1 | tee azd-deploy.log
```

Cerca errori di build Docker (spesso su `api-maf-python` per uv/Python, o `mcp-destination-recommendation` per Maven/Java).

## 4. Verifica che le immagini siano state pushate nell'ACR

```bash
az acr repository list --name cruacsilxfvk3jw -o table
az acr repository show-tags --name cruacsilxfvk3jw --repository ui-angular -o table
```

Se non vedi i repository/tag dei servizi -> build non è mai arrivato a push.

## 5. Verifica i log del container app per errori di pull

```bash
az containerapp logs show -g rg-azure-ai-travel-agents-eastus -n ui-angular --tail 100
```

## Causa radice più probabile nel tuo caso

Dal log: dopo il purge del Cognitive Services, è ripartito `azd up` da capo. Se ha completato `provision` ma ha avuto un errore durante `deploy` (build lunghi in sandbox arm64 -> amd64), i Container Apps restano alla revision iniziale con hello-world.

**Fix rapido:** rilancia solo `azd deploy --no-prompt` (non serve rifare il provision).
