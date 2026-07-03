// Sample parameters for the NimBus management WebApp infrastructure.
// Deploy AFTER deploy.core.bicep — several inputs below are produced by the
// core deployment and Application Insights; docs/deployment.md ("Raw Bicep")
// lists the az commands that yield each placeholder value.
//   az deployment group create \
//     --resource-group rg-nimbus-dev \
//     --template-file ../deploy.webapp.bicep \
//     --parameters deploy.webapp.example.bicepparam
using '../deploy.webapp.bicep'

param solutionId = 'nimbus'
param environment = 'dev'

// Free-form version string surfaced in the WebApp UI/settings.
param webAppVersion = 'manual'

// Application Insights values (az monitor app-insights api-key create / component show).
param apiKey = '<app-insights-api-key>'
param appInsightsAppId = '<app-insights-app-id>'
param instrumentationKey = '<app-insights-instrumentation-key>'

// Exactly ONE of cosmosAccountEndpoint / sqlConnectionString must be non-empty.
// Cosmos endpoint: az cosmosdb show --query documentEndpoint
param cosmosAccountEndpoint = 'https://cosmos-nimbus-dev.documents.azure.com:443/'

// Convention: sb-{solutionId}-{environment}.servicebus.windows.net
param serviceBusFullyQualifiedNamespace = 'sb-nimbus-dev.servicebus.windows.net'

// locationParam only affects net-new resources; the WebApp must live in the
// same region as the management plan created by deploy.core.bicep.
param locationParam = 'westeurope'
