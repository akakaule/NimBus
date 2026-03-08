param skuName string
param name string
param location string = resourceGroup().location

resource appServicePlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: name
  location: location
  sku: {
    name: skuName
  }
}

output id string = appServicePlan.id
