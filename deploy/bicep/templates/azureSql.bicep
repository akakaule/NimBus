// Provisions an Azure SQL server + database for the NimBus message store.
// Default tier S0 — adequate for typical message volumes; tune up via param for
// production scale.

param serverName string
param databaseName string
param location string
param administratorLogin string
@secure()
param administratorPassword string

@allowed([
  'Basic'
  'S0'
  'S1'
  'S2'
])
param skuName string = 'S0'

// 'Disabled' only in the private network state (spec 034 §5.13).
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
  }
}

// Allow Azure services (App Service, Functions) to reach the server. The rule admits
// every Azure tenant, so it exists only while public access does. An incremental
// deployment does not delete it when it drops out of the template; the nb CLI deletes
// it explicitly when a server switches to private.
resource allowAzureRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (publicNetworkAccess == 'Enabled') {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: skuName
  }
}

output id string = sqlServer.id
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = sqlDatabase.name
