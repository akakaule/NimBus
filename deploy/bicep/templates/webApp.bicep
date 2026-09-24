param appName string
param appServicePlanId string
param location string = resourceGroup().location
param alwaysOn bool = true
param settings array = []
@secure()
param secretSettings object = {}

var secretAppSettings = [for setting in items(secretSettings): {
  name: setting.key
  value: setting.value
}]

var appsettings = concat(settings, secretAppSettings, [
  {
    name: 'WEBSITE_RUN_FROM_PACKAGE'
    value: '1'
  }
])

// Private networking (spec 034). Empty keeps the app off the VNet, as in public mode.
param virtualNetworkSubnetId string = ''

@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

// allTraffic, not the legacy vnetRouteAllEnabled: it also routes configuration traffic,
// including managed-identity token requests, through the customer's firewall.
var vnetProperties = empty(virtualNetworkSubnetId) ? {} : {
  virtualNetworkSubnetId: virtualNetworkSubnetId
  outboundVnetRouting: {
    allTraffic: true
  }
}

resource webApplication 'Microsoft.Web/sites@2024-11-01' = {
  name: appName
  location: location
  kind: 'web'
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'hidden-related:${resourceGroup().id}/providers/Microsoft.Web/serverfarms/appServicePlan': 'Resource'
  }
  properties: union({
    serverFarmId: appServicePlanId
    siteConfig: {
      alwaysOn: alwaysOn
      ftpsState:'FtpsOnly'
      // Azure's Windows default is a 32-bit worker (a density default from the
      // shared-tier era); nothing in NimBus needs it and it caps the process
      // address space, so run the .NET app 64-bit.
      use32BitWorkerProcess: false
      appSettings:appsettings
    }
    httpsOnly: true
    publicNetworkAccess: publicNetworkAccess
  }, vnetProperties)
}

output id string = webApplication.id
output name string = webApplication.name
output identity string = webApplication.identity.principalId
