param name string
param location string = resourceGroup().location

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

output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${name};AccountKey=${listKeys(storageaccount.id, storageaccount.apiVersion).keys[0].value};EndpointSuffix=core.windows.net'
output storageId string = storageaccount.id
