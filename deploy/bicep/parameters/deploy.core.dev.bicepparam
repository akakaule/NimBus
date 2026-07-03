// Sample parameters for a dev deployment of the NimBus core infrastructure.
// Deploy with (Azure CLI >= 2.53.0, Bicep CLI >= 0.22.6):
//   az deployment group create \
//     --resource-group rg-nimbus-dev \
//     --template-file ../deploy.core.bicep \
//     --parameters deploy.core.dev.bicepparam
// See docs/deployment.md ("Raw Bicep") for the full self-service walkthrough.
using '../deploy.core.bicep'

// Woven into every resource name: {type}-{solutionId}-{environment}[-suffix].
// Keep both lowercase alphanumeric. The Functions storage account name
// 'st{solutionId}{environment}func' must stay within 24 characters.
param solutionId = 'nimbus'
param environment = 'dev'

// Endpoint id of the central resolver. Must match the compiled-in constant
// (src/NimBus.Core/Constants.cs: ResolverId = 'Resolver').
param resolverId = 'Resolver'

// Salts the Elastic Premium content-share name. Any string works; pass a fresh
// value (e.g. a build id) per deployment when using the ElasticPremium plan:
//   --parameters deploy.core.dev.bicepparam uniqueDeploy=$BUILD_ID
param uniqueDeploy = 'manual-dev'

// Region for net-new resources. Existing resources must keep their region —
// pass the per-resource *Location override params if yours differ.
param locationParam = 'westeurope'

// Dev relies on the defaults:
//   storageProvider   = 'cosmos'
//   resolverPlan      = 'FlexConsumption'  (FC1, scale-to-zero Linux)
//   managementPlanSku = ''                 -> 'B1' for dev/development
