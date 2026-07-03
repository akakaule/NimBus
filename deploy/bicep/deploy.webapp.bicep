
param solutionId string
param environment string = 'dev'
param webAppVersion string
param apiKey string
param appInsightsAppId string
param instrumentationKey string
// Cosmos endpoint is optional now: empty when the active provider is SQL Server.
param cosmosAccountEndpoint string = ''
// SQL connection string. Empty when the active provider is Cosmos. Exactly one of
// cosmosAccountEndpoint and sqlConnectionString must be non-empty. Secret-marked so
// it does not appear in deployment history; production deployments should pass a
// Key Vault reference (e.g. @Microsoft.KeyVault(...)) instead of a literal string.
@secure()
param sqlConnectionString string = ''
param serviceBusFullyQualifiedNamespace string
param locationParam string = 'westeurope'

// Optional bootstrap admin for NimBus.Extensions.Identity. When both are set AND
// the active storage provider is SQL Server, the WebApp is configured to use
// ASP.NET Core Identity-backed username/password sign-in; the initializer hosted
// service seeds this single admin on first boot. Leave empty to keep the WebApp
// on its existing auth path (Entra ID / local-dev).
param identityAdminEmail string = ''
@secure()
param identityAdminPassword string = ''

// Per-resource location override. Empty means "use the global locationParam".
// The CLI pins this to the existing web app's location when one is already
// present so we don't try to migrate it to a different region.
param webAppLocation string = ''

// The management App Service Plan location, supplied by the CLI. The WebApp
// MUST live in the same region as its plan (Azure rejects cross-region
// references with a NotFound error on the serverFarm), so when no explicit
// webAppLocation is supplied, we fall back to the plan's location instead of
// the global default.
param managementAppServicePlanLocation string = ''

// Always On keeps the WebApp warm. Supported on Basic and above; the CLI passes
// false when the management plan runs on a Free/Shared SKU (F1/D1).
param alwaysOnEnabled bool = true

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam
var effectivePlanLocation = empty(managementAppServicePlanLocation) ? location : managementAppServicePlanLocation
var effectiveWebAppLocation = empty(webAppLocation) ? effectivePlanLocation : webAppLocation

var sbNamespace = 'sb-${toLower(solutionId)}-${toLower(environment)}'

var appServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var managementWebAppName = 'webapp-${toLower(solutionId)}-${toLower(environment)}-management'

var cosmosAccountName = 'cosmos-${toLower(solutionId)}-${toLower(environment)}'

var isDevelopmentEnvironment = contains([
  'dev'
  'development'
], toLower(environment))

//##############################################
// Create Web App: Conflict Resolution Web App
//##############################################

// Validate exactly one of the storage settings is supplied. Empty strings on both
// sides would silently leave the WebApp without a storage backend.
var hasCosmos = !empty(cosmosAccountEndpoint)
var hasSql = !empty(sqlConnectionString)
var validStorageInput = (hasCosmos && !hasSql) || (!hasCosmos && hasSql)

#disable-next-line BCP088
var storageValidation = validStorageInput ? '' : '[ERROR] Exactly one of cosmosAccountEndpoint or sqlConnectionString must be provided.'

var coreWebAppSettings = [
  {
    name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
    value: serviceBusFullyQualifiedNamespace
  }
  {
    name: 'ServiceBusNamespace'
    value: sbNamespace
  }
  {
    name: 'UnresolvedEventLimit'
    value: '50'
  }
  {
    name: 'AppInsights:ApiKey'
    value: apiKey
  }
  {
    name: 'Environment'
    value: environment
  }
  {
    name: 'AppInsights:ApplicationId'
    value: appInsightsAppId
  }
  {
    name: 'WebAppVersion'
    value: webAppVersion
  }
  {
    name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
    value: instrumentationKey
  }
]

var cosmosSetting = hasCosmos ? [
  {
    name: 'CosmosAccountEndpoint'
    value: cosmosAccountEndpoint
  }
] : []

var sqlSetting = hasSql ? [
  {
    name: 'SqlConnection'
    value: sqlConnectionString
  }
] : []

// NimBus Identity (username/password) is wired only on SQL deployments and only
// when a bootstrap admin email is supplied. The connection string reuses the
// MessageDatabase; Identity tables live under a separate schema (default
// 'nimbus') so they don't collide with the message-store schema.
var identityEnabled = hasSql && !empty(identityAdminEmail)
var identitySettings = identityEnabled ? [
  {
    name: 'NimBusIdentity__ConnectionString'
    value: sqlConnectionString
  }
  {
    name: 'NimBusIdentity__RequireEmailConfirmation'
    value: 'false'
  }
  {
    name: 'NimBusIdentity__Bootstrap__Email'
    value: identityAdminEmail
  }
  {
    name: 'NimBusIdentity__Bootstrap__Password'
    value: identityAdminPassword
  }
] : []

var baseWebAppSettings = concat(coreWebAppSettings, cosmosSetting, sqlSetting, identitySettings)

var developmentDiagnosticSettings = isDevelopmentEnvironment ? [
  {
    name: 'ASPNETCORE_DETAILEDERRORS'
    value: 'true'
  }
  {
    name: 'ASPNETCORE_CAPTURESTARTUPERRORS'
    value: 'true'
  }
] : []

var webappsettings = concat(baseWebAppSettings, developmentDiagnosticSettings)

module webAppModule 'templates/webApp.bicep' = {
  name: 'webAppDeploy'
  params: {
    appName:managementWebAppName
    appServicePlanId:'/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.Web/serverfarms/${appServicePlanName}'
    location:effectiveWebAppLocation
    alwaysOn: alwaysOnEnabled
    settings:webappsettings
  }
}

//##############################################
// WebApp: RBAC role assignments
//##############################################

module webAppRoleAssignments 'templates/roleAssignments.bicep' = {
  name: 'webAppRoleAssignmentsDeploy'
  params: {
    serviceBusNamespaceName: sbNamespace
    cosmosAccountName: hasCosmos ? cosmosAccountName : ''
    principalId: webAppModule.outputs.identity
    storageProvider: hasCosmos ? 'cosmos' : 'sqlserver'
  }
}
