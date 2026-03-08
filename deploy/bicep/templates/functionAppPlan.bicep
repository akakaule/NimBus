param skuName string
param name string
param location string = resourceGroup().location

resource functionAppServicePlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: name
  location: location
  kind: 'elastic'
  sku: {
    name: skuName    
  }
  properties:{
    maximumElasticWorkerCount: 10
  }
}

output id string = functionAppServicePlan.id 
