param name string
param location string = resourceGroup().location

// When the resolver is hosted on Flex Consumption, it needs a blob container
// that holds its zipped app package. We provision it here so the Function App
// can reference it via SystemAssignedIdentity at create time.
param createDeploymentContainer bool = false
param deploymentContainerName string = 'app-package-resolver'

resource storageaccount 'Microsoft.Storage/storageAccounts@2021-09-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2021-09-01' = if (createDeploymentContainer) {
  parent: storageaccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2021-09-01' = if (createDeploymentContainer) {
  parent: blobService
  name: deploymentContainerName
}

output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${name};AccountKey=${listKeys(storageaccount.id, storageaccount.apiVersion).keys[0].value};EndpointSuffix=core.windows.net'
output storageId string = storageaccount.id
output storageName string = storageaccount.name
output blobEndpoint string = storageaccount.properties.primaryEndpoints.blob
