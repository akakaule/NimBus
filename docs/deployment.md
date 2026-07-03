# Deploying NimBus

How to stand up the NimBus platform (Service Bus, message store, resolver Function App, management WebApp) in your own Azure subscription — from a laptop, from CI/CD pipelines, or by driving the Bicep templates directly.

## Choose a path

| Path | Best for | What you run |
|---|---|---|
| [One command](#path-1-one-command-from-a-clone) | Trying NimBus, dev environments | `dnx Akaule.NimBus.CommandLine -- setup ...` from a repo clone |
| [GitHub Actions](#path-2-github-actions) | Teams on GitHub, OIDC (no stored secrets) | The included `Deploy NimBus` workflow |
| [Azure DevOps](#path-3-azure-devops) | Enterprises on Azure DevOps | The included `pipelines/azure-pipelines-deploy.yml` |
| [Raw Bicep](#path-4-raw-bicep-self-service) | Platform teams with their own IaC tooling | `az deployment group create` + the sample `.bicepparam` files |

## What a deployment consists of

Every path ultimately performs the same three layers, in order:

1. **Azure resources** — `deploy/bicep/deploy.core.bicep` (Service Bus namespace, App Insights, Cosmos DB *or* Azure SQL, Functions storage, hosting plans, resolver Function App, RBAC) followed by `deploy/bicep/deploy.webapp.bicep` (management WebApp + RBAC). Pure Bicep; this layer is replaceable by your own tooling.
2. **Service Bus topology** — `nb topology apply` creates the topics, session-enabled subscriptions, and SQL routing rules for every endpoint. The topology is *generated from the compiled `PlatformConfiguration`* — it cannot be expressed in Bicep, so this step always uses the `nb` CLI (or runs in-process, as the Aspire sample's provisioner does).
3. **Application code** — `nb deploy apps` publishes `src/NimBus.Resolver` and `src/NimBus.WebApp` and zip-deploys them.

`nb setup` chains all three. The CLI is defensive about existing environments: resources are pinned to their current region, hosting plans to their current plan type/SKU (see [docs/cli.md](cli.md) for the pinning rules).

## Prerequisites (all paths)

> Full reference for governance review — complete resource inventory, resource provider registrations, and the role-assignment matrix: **[Azure Infrastructure Requirements](azure-requirements.md)**.

**Tooling**

- Azure CLI ≥ 2.60.0 **required** for Flex Consumption deploys ([Microsoft-documented minimum](https://learn.microsoft.com/azure/azure-functions/flex-consumption-how-to); `nb` checks this before publishing). ≥ 2.70 recommended; `.bicepparam` support needs ≥ 2.53
- .NET 10 SDK and Node.js 22 wherever the apps are built (pipelines set these up themselves)

**RBAC for the deploying identity.** The Bicep creates role assignments (`Microsoft.Authorization/roleAssignments`: Azure Service Bus Data Owner and, on Flex Consumption, Storage Blob Data Owner), and plain **Contributor cannot write role assignments**. On the target resource group, grant the pipeline/service principal either:

- **Owner**, or
- least-privilege: **Contributor + Role Based Access Control Administrator** (role id `f58310d9-a9f6-439a-9e8d-f62e7b41a168`), ideally with an [ABAC condition](https://learn.microsoft.com/azure/role-based-access-control/delegate-role-assignments-overview) restricting assignable roles to Azure Service Bus Data Owner (`090c5cfd-751d-490a-894a-3ce6f1109419`) and Storage Blob Data Owner (`b7e6dc6d-f1e8-4753-8033-0f276bb0955b`).

Cosmos data-plane role assignments (`Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments`) live under the DocumentDB provider and are covered by Contributor.

**Resource provider registration.** On a fresh subscription, all providers the deployment uses must be registered — ARM auto-registers them only when the deploying identity has subscription-scope permission, which resource-group-scoped pipeline identities lack. Pre-register once per subscription with an admin account ([full provider list](azure-requirements.md#resource-provider-registrations)):

```bash
for ns in Microsoft.ServiceBus Microsoft.Web Microsoft.Storage Microsoft.Insights Microsoft.DocumentDB Microsoft.EventGrid; do
  az provider register --namespace $ns
done
```

`Microsoft.EventGrid` backs only the optional storage-hook webhooks; `nb infra apply` tries to register it and warns-and-continues when the identity lacks permission.

## Path 1: One command from a clone

```bash
git clone https://github.com/akakaule/NimBus && cd NimBus
dnx Akaule.NimBus.CommandLine -- setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

`dnx` ships with the .NET 10 SDK and runs the published CLI without installing it (the `--` separator is required). Full option reference, including `--resolver-plan`, `--management-plan-sku`, and the SQL Server storage variants: [docs/cli.md](cli.md).

## Path 2: GitHub Actions

The repository ships [.github/workflows/deploy.yml](../.github/workflows/deploy.yml) — a manually triggered (`workflow_dispatch`) workflow that logs in with OIDC (no stored secrets) and runs `infra apply` → `topology apply` → `deploy apps`.

### 2.1 Create the Entra identity (once)

```bash
APP_ID=$(az ad app create --display-name "nimbus-github-deploy" --query appId -o tsv)
az ad sp create --id $APP_ID
SP_OBJECT_ID=$(az ad sp show --id $APP_ID --query id -o tsv)

# Grant RBAC on the target resource group (see Prerequisites; Owner also works)
az role assignment create \
  --role "Contributor" \
  --assignee-object-id $SP_OBJECT_ID --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RESOURCE_GROUP>
az role assignment create \
  --role "Role Based Access Control Administrator" \
  --assignee-object-id $SP_OBJECT_ID --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RESOURCE_GROUP>
```

### 2.2 Add a federated credential per environment

The workflow job runs with `environment: ${{ inputs.environment }}`, so the OIDC token's subject is `repo:OWNER/REPO:environment:<name>` — **each environment name you deploy (dev, prod, ...) needs its own federated credential** whose subject matches exactly (no wildcards; a mismatch fails at token exchange, not at creation; max 20 credentials per app):

```bash
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:OWNER/REPO:environment:dev",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

### 2.3 Configure repository variables

Under **Settings → Secrets and variables → Actions → Variables**, set:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | The app registration's Application (client) ID (`$APP_ID`) |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Target subscription ID |

Environment-level variables override repository-level ones, so dev and prod can use different app registrations by defining `AZURE_CLIENT_ID` on each GitHub environment.

### 2.4 Run it

**Actions → Deploy NimBus → Run workflow** with your solution id, environment, and resource group. Optional inputs: `resolver-plan` (ElasticPremium | FlexConsumption), `management-plan-sku` (e.g. B1, S1, P1v3), and `location` — leave empty for the defaults and existing-plan pinning.

## Path 3: Azure DevOps

The repository ships [pipelines/azure-pipelines-deploy.yml](../pipelines/azure-pipelines-deploy.yml) — a manually triggered pipeline that runs `nb setup` in one `AzureCLI@2` task.

1. **Create a service connection** — Project settings → Service connections → New service connection → **Azure Resource Manager** → *App registration (automatic)* with credential **Workload identity federation** (the recommended, secret-free default; the built-in `AzureCLI@2` task works with it unchanged). Scope it to the subscription or directly to the target resource group, then grant the generated identity the RBAC from [Prerequisites](#prerequisites-all-paths). The automatic flow requires sufficient permissions in Entra; orgs that block app registrations can use the *Managed identity* option instead. (The `az devops` CLI cannot create workload-identity connections — use the portal.)
2. **Import the pipeline** — Pipelines → New pipeline → select your repo → Existing Azure Pipelines YAML file → `pipelines/azure-pipelines-deploy.yml`.
3. **Run it** with your solution id, environment, resource group, and the service connection name. The same optional `resolverPlan` / `managementPlanSku` / `location` parameters are available.

## Path 4: Raw Bicep (self-service)

For platform teams that provision Azure resources with their own tooling. Raw Bicep covers **layer 1 only** — the Service Bus topology and app deployment still need the `nb` CLI afterwards.

Sample parameter files live in [`deploy/bicep/parameters/`](../deploy/bicep/parameters/) (`.bicepparam` deployment needs Azure CLI ≥ 2.53, Bicep ≥ 0.22.6).

### 4.1 Core infrastructure

```bash
az deployment group create \
  --resource-group rg-nimbus-dev \
  --template-file deploy/bicep/deploy.core.bicep \
  --parameters deploy/bicep/parameters/deploy.core.dev.bicepparam
```

Parameter notes (see the comments in the sample files):
- `resolverId` must be `Resolver` — it matches the compiled-in constant `src/NimBus.Core/Constants.cs`.
- `solutionId`/`environment` are woven into every resource name; the storage account `st{solutionId}{environment}func` must stay ≤ 24 characters, lowercase alphanumeric.
- Defaults: `storageProvider=cosmos`, `resolverPlan=FlexConsumption`, `managementPlanSku` empty → B1 for dev/development, S1 otherwise. **ElasticPremium ↔ FlexConsumption cannot be converted in place** — to switch, delete the resolver Function App *and* the core App Service Plan first.

### 4.2 WebApp infrastructure

`deploy.webapp.bicep` needs values produced by the core deployment. Obtain them the same way the CLI does (`InfrastructureDeployer.cs`):

```bash
az extension add --name application-insights --upgrade

# App Insights API key (recreate if it already exists — keys are shown once)
APIKEY=$(az monitor app-insights api-key create \
  --app ai-nimbus-dev-global-tracelog --resource-group rg-nimbus-dev \
  --api-key management-app \
  --read-properties ReadTelemetry --write-properties WriteAnnotations \
  --query apiKey -o tsv)

APP_ID=$(az monitor app-insights component show \
  --app ai-nimbus-dev-global-tracelog --resource-group rg-nimbus-dev --query appId -o tsv)
IKEY=$(az monitor app-insights component show \
  --app ai-nimbus-dev-global-tracelog --resource-group rg-nimbus-dev --query instrumentationKey -o tsv)
COSMOS_ENDPOINT=$(az cosmosdb show \
  --resource-group rg-nimbus-dev --name cosmos-nimbus-dev --query documentEndpoint -o tsv)

az deployment group create \
  --resource-group rg-nimbus-dev \
  --template-file deploy/bicep/deploy.webapp.bicep \
  --parameters deploy/bicep/parameters/deploy.webapp.example.bicepparam \
  --parameters apiKey="$APIKEY" appInsightsAppId="$APP_ID" instrumentationKey="$IKEY" \
    cosmosAccountEndpoint="$COSMOS_ENDPOINT"
```

The Service Bus namespace follows the convention `sb-{solutionId}-{environment}.servicebus.windows.net`.

### 4.3 Topology and apps (still required)

```bash
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
nb deploy apps   --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Both must run from a repository clone (`nb deploy apps` publishes the resolver and WebApp from source; the WebApp SPA build needs Node.js 22).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `The existing core App Service Plan is X but --resolver-plan requested Y` | Azure cannot convert ElasticPremium ↔ FlexConsumption in place. Delete the resolver Function App **and** the core plan, then redeploy. |
| `InvalidResourceLocation` / region errors | A same-named resource exists in another region. The CLI pins existing resources automatically; raw-Bicep users must pass the per-resource `*Location` override params. To actually move a resource, delete it first. |
| `Failed to register Azure provider` warning | The identity lacks subscription-level `/register` permission. Harmless unless you use Event Grid storage hooks — then pre-register once per subscription (see Prerequisites). |
| SQL server name conflict after deleting an environment | Azure SQL server DNS names are held globally for 24–72 h after deletion. Use `--sql-server-name` (or the `sqlServerName` param) to pick a fresh name. |
| Flex Consumption zip deploy fails with `SSLEOFError` / "Certificate verification failed … behind a proxy" against `<app>.scm.azurewebsites.net` | The local Azure CLI is < 2.60.0 and pushed to the legacy Kudu zipdeploy endpoint — the proxy/certificate hint is a red herring. Run `az upgrade` (or `winget upgrade Microsoft.AzureCLI`) and retry. `nb` fails fast on this before publishing. |
| Resolver zip deploy reports failure on Flex Consumption | Update the Azure CLI (≥ 2.70 recommended). Do not stop the app before deploying — the CLI health-checks the running host after publishing. |
| Role assignment errors during Bicep deployment | The deploying identity lacks `Microsoft.Authorization/roleAssignments/write`. See [Prerequisites](#prerequisites-all-paths). |
