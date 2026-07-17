param appName string
param appServicePlanId string
param location string = resourceGroup().location
param settings array = []
@secure()
param secretSettings object = {}
@secure()
param storageConnectionString string
@secure()
param appInsightsInstrumentationKey string
param functionAppVersion string = '4'

var secretAppSettings = [for setting in items(secretSettings): {
  name: setting.key
  value: setting.value
}]

var appsettings = concat(settings, secretAppSettings, [
  {
      name: 'AzureWebJobsStorage'
      value: storageConnectionString
  }
  {
      name: 'FUNCTIONS_WORKER_RUNTIME'
      value: 'dotnet-isolated'
  }
  {
      name: 'FUNCTIONS_EXTENSION_VERSION'
      value: '~${functionAppVersion}'
  }
  {
      name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
      value: appInsightsInstrumentationKey
  }
])

resource azureFunction 'Microsoft.Web/sites@2022-03-01' = {
  name: appName
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlanId
    siteConfig: {
      ftpsState:'FtpsOnly'
      appSettings:appsettings
      netFrameworkVersion: 'v10.0'
    }
  }
}

output webAppUri string = azureFunction.properties.hostNames[0]
output principalId string = azureFunction.identity.principalId
