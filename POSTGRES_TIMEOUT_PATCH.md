# Configurable Postgres Command Timeout — azure-mcp patch

This fork adds a **configurable command timeout** to the Azure MCP Postgres tools so that
long-running queries (e.g. `COUNT(*)` across many large tables) don't fail with the default
Npgsql 30-second timeout (`NpgsqlException: Exception while reading from stream`).

## What changed

The timeout is resolved with this precedence (first match wins):

1. **Per-call parameter** — the agent passes `command-timeout: <seconds>` to the Postgres query tool.
2. **Environment variable** — `AZURE_MCP_POSTGRES_COMMAND_TIMEOUT=<seconds>` on the container.
3. **Npgsql default** — 30 seconds when neither is set.

`0` means **infinite** (no timeout).

### Files modified (`tools/Azure.Mcp.Tools.Postgres/src`)

| File | Change |
| --- | --- |
| `Providers/IDbProvider.cs` | `GetCommand(...)` gains `int? commandTimeoutSeconds = null` |
| `Providers/DbProvider.cs` | Resolves timeout (param → env var → default); sets `command.CommandTimeout` |
| `Services/IPostgresService.cs` | `ExecuteQueryAsync(...)` gains `int? commandTimeoutSeconds` |
| `Services/PostgresService.cs` | Passes the timeout through to `GetCommand(...)` |
| `Options/PostgresOptionDefinitions.cs` | Adds `CommandTimeoutDescription` |
| `Options/Database/DatabaseQueryOptions.cs` | Adds `CommandTimeout` option → `command-timeout` tool param |
| `Commands/Database/DatabaseQueryCommand.cs` | Passes `options.CommandTimeout` into `ExecuteQueryAsync` |

A self-contained [`Dockerfile.timeout`](./Dockerfile.timeout) builds the patched server image.

---

## Build & push to ACR **without local Docker**

`az acr build` builds the image **in the cloud** on native amd64 agents — no local Docker daemon
and no slow QEMU emulation required.

```bash
# Variables — adjust to your environment
SUBSCRIPTION_ID="28a4d80c-cdd3-45e6-af8b-20b6a31598f1"
RESOURCE_GROUP="rg-foundrymcp-postgres"
LOCATION="centralindia"
ACR_NAME="azmcppostgresacr"          # must be globally unique, 5-50 alphanumeric
IMAGE="azure-mcp-postgres-timeout:v1"

az account set --subscription "$SUBSCRIPTION_ID"

# 1. Create the registry (once). Basic SKU is fine.
az acr create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$ACR_NAME" \
  --sku Basic

# 2. Cloud build straight from this repo (run from the repo root).
#    --platform linux/amd64 matches Azure Container Apps.
az acr build \
  --registry "$ACR_NAME" \
  --image "$IMAGE" \
  --file Dockerfile.timeout \
  --platform linux/amd64 \
  --build-arg RID=linux-musl-x64 \
  .
```

The resulting image is `"$ACR_NAME".azurecr.io/azure-mcp-postgres-timeout:v1`.

---

## Set it up in Azure Container Apps (ACA)

### 1. Let the container app pull from ACR (managed identity + AcrPull)

```bash
CONTAINER_APP_NAME="azmcp-postgres-server-alsuxlx5qf"
# Principal ID of the container app's managed identity
CA_PRINCIPAL_ID="e3d29b57-38ee-4379-bdbe-c21e89bec479"

ACR_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv)

az role assignment create \
  --assignee "$CA_PRINCIPAL_ID" \
  --role AcrPull \
  --scope "$ACR_ID"
```

### 2. Point the container app at the new image + set the default timeout

```bash
az containerapp registry set \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --server "$ACR_NAME".azurecr.io \
  --identity system

az containerapp update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "$ACR_NAME".azurecr.io/"$IMAGE" \
  --set-env-vars AZURE_MCP_POSTGRES_COMMAND_TIMEOUT=300
```

A new revision rolls out with the patched image and a 300s default timeout.

### Alternative: infrastructure-as-code (Bicep)

If you deploy via `azd`/Bicep, update the image reference and add the env var in your
`aca-infrastructure.bicep` module, then run `azd up`:

```bicep
// container image
image: '${acrName}.azurecr.io/azure-mcp-postgres-timeout:v1'

// add to the container's env array
{
  name: 'AZURE_MCP_POSTGRES_COMMAND_TIMEOUT'
  value: '300'
}
```

---

## Using the per-call timeout from an agent

The agent can override the timeout for an individual query by passing the
`command-timeout` parameter (seconds; `0` = infinite):

```jsonc
{
  "subscription": "...",
  "resource-group": "rg-postgresdb",
  "server": "amanppostgres",
  "database": "postgres",
  "user": "azmcp-postgres-server-alsuxlx5qf",
  "query": "SELECT count(*) FROM powerbi.table1",
  "command-timeout": 600
}
```
