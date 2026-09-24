param appName string
param appServicePlanId string
param location string = resourceGroup().location
param settings array = []
@secure()
param secretSettings object = {}

// Identity-based access to AzureWebJobsStorage and the deployment package container.
param storageAccountName string
param deploymentStorageBlobUri string

@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048
param maximumInstanceCount int = 100

// Private networking (spec 034). Empty keeps the app off the VNet, as in public mode.
// Flex Consumption routes all outbound traffic into the integrated VNet by itself and
// scales on VNet-restricted triggers natively, so no routing or scale-monitoring
// setting is needed. The subnet must be delegated to Microsoft.App/environments.
param virtualNetworkSubnetId string = ''

@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

// Flex Consumption-specific layout: runtime, scaling, and the deployment package
// container live under properties.functionAppConfig — NOT siteConfig.
// Required app settings (no FUNCTIONS_WORKER_RUNTIME / FUNCTIONS_EXTENSION_VERSION
// / WEBSITE_CONTENTAZUREFILECONNECTIONSTRING / WEBSITE_CONTENTSHARE — those are
// rejected on Flex).
var secretAppSettings = [for setting in items(secretSettings): {
  name: setting.key
  value: setting.value
}]

var flexAppSettings = concat(settings, secretAppSettings, [
  {
    name: 'AzureWebJobsStorage__accountName'
    value: storageAccountName
  }
])

resource azureFunction 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: union({
    serverFarmId: appServicePlanId
    publicNetworkAccess: publicNetworkAccess
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: deploymentStorageBlobUri
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
        alwaysReady: []
      }
    }
    siteConfig: {
      ftpsState: 'FtpsOnly'
      appSettings: flexAppSettings
      minTlsVersion: '1.2'
    }
    httpsOnly: true
  }, empty(virtualNetworkSubnetId) ? {} : {
    virtualNetworkSubnetId: virtualNetworkSubnetId
  })
}

output webAppUri string = azureFunction.properties.hostNames[0]
output principalId string = azureFunction.identity.principalId
output id string = azureFunction.id
