
param solutionId string
param environment string = 'dev'
param webAppVersion string
param apiKey string
param appInsightsAppId string
param instrumentationKey string
param cosmosDbConnectionString string
param managerServiceBusConnection string
param locationParam string = 'westeurope'

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam

var sbNamespace = 'sb-${toLower(solutionId)}-${toLower(environment)}'

var appServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var managementWebAppName = 'webapp-${toLower(solutionId)}-${toLower(environment)}-management'

//##############################################
// Create Web App: Conflict Resolution Web App
//##############################################

var webappsettings = [
  {
    name: 'AzureWebJobsServiceBus'
    value: managerServiceBusConnection
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
    name: 'CosmosConnection'
    value: cosmosDbConnectionString
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
