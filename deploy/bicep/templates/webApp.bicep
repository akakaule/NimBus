param appName string
param appServicePlanId string
param location string = resourceGroup().location
param alwaysOn bool = true
param settings array = []

var appsettings = concat(settings,[
  {
    name: 'WEBSITE_RUN_FROM_PACKAGE'
    value: '1'
  }
])

resource webApplication 'Microsoft.Web/sites@2022-03-01' = {
  name: appName
  location: location
  kind: 'web'
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'hidden-related:${resourceGroup().id}/providers/Microsoft.Web/serverfarms/appServicePlan': 'Resource'
  }
  properties: {    
    serverFarmId: appServicePlanId
    siteConfig: {
      alwaysOn: alwaysOn
      ftpsState:'FtpsOnly'
      appSettings:appsettings
    }
    httpsOnly: true    
  }
}

output name string = webApplication.name
output identity string = webApplication.identity.principalId
