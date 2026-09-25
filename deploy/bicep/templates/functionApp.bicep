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

// Per-app instance ceiling (siteConfig.functionAppScaleLimit) on the Elastic
// Premium plan. 0 is the platform's documented "unrestricted" value and is written
// explicitly: a null property would be dropped from the request and a cap applied
// by an earlier deployment would silently stay in place.
@minValue(0)
param functionAppScaleLimit int = 0

// Private networking (spec 034). Empty keeps the app off the VNet, as in public mode.
param virtualNetworkSubnetId string = ''

@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

var isVnetIntegrated = !empty(virtualNetworkSubnetId)

// allTraffic, not the legacy vnetRouteAllEnabled: the legacy flag routes application
// traffic only, while allTraffic also routes configuration traffic (the content share,
// managed-identity token requests) into the VNet, where the customer's firewall decides
// what leaves.
var vnetProperties = isVnetIntegrated ? {
  virtualNetworkSubnetId: virtualNetworkSubnetId
  outboundVnetRouting: {
    allTraffic: true
    contentShareTraffic: true
  }
} : {}

// A VNet-restricted trigger never scales past the prewarmed instance count unless the
// runtime monitors scale itself; the Service Bus extension supports it.
var vnetSiteConfig = isVnetIntegrated ? {
  functionsRuntimeScaleMonitoringEnabled: true
} : {}

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

resource azureFunction 'Microsoft.Web/sites@2024-11-01' = {
  name: appName
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: union({
    serverFarmId: appServicePlanId
    siteConfig: union({
      ftpsState:'FtpsOnly'
      appSettings:appsettings
      netFrameworkVersion: 'v10.0'
      minTlsVersion: '1.2'
      functionAppScaleLimit: functionAppScaleLimit
    }, vnetSiteConfig)
    httpsOnly: true
    publicNetworkAccess: publicNetworkAccess
  }, vnetProperties)
}

output webAppUri string = azureFunction.properties.hostNames[0]
output principalId string = azureFunction.identity.principalId
output id string = azureFunction.id
