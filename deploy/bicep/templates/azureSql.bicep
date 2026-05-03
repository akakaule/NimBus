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

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// Allow Azure services (App Service, Functions) to reach the server. Production
// deployments should replace this with private endpoints; left open here for the
// default operator experience to match the Cosmos parity.
resource allowAzureRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
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

output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = sqlDatabase.name
