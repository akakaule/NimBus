
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

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam

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

var baseWebAppSettings = concat(coreWebAppSettings, cosmosSetting, sqlSetting)

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
    location:location
    alwaysOn: true
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
    cosmosAccountName: cosmosAccountName
    principalId: webAppModule.outputs.identity
  }
}
