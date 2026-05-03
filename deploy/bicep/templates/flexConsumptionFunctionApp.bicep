param appName string
param appServicePlanId string
param location string = resourceGroup().location
param settings array = []

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

// Flex Consumption-specific layout: runtime, scaling, and the deployment package
// container live under properties.functionAppConfig — NOT siteConfig.
// Required app settings (no FUNCTIONS_WORKER_RUNTIME / FUNCTIONS_EXTENSION_VERSION
// / WEBSITE_CONTENTAZUREFILECONNECTIONSTRING / WEBSITE_CONTENTSHARE — those are
// rejected on Flex).
var flexAppSettings = concat(settings, [
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
  properties: {
    serverFarmId: appServicePlanId
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
    }
  }
}

output webAppUri string = azureFunction.properties.hostNames[0]
output principalId string = azureFunction.identity.principalId
