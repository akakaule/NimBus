
param solutionId string
param environment string = 'dev'
param webAppVersion string
param apiKey string
param appInsightsAppId string
param instrumentationKey string
param cosmosAccountEndpoint string
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

//##############################################
// Create Web App: Conflict Resolution Web App
//##############################################

var webappsettings = [
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
  {
    name: 'CosmosAccountEndpoint'
    value: cosmosAccountEndpoint
  }
]

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
