param serviceBusNamespaceName string
param cosmosAccountName string = ''
param principalId string

@allowed([
  'cosmos'
  'sqlserver'
])
param storageProvider string = 'cosmos'

// ----------------------------------------------------------------------------
// Service Bus Data Owner — required regardless of storage provider so the
// resolver identity can receive and complete messages via managed identity.
// ----------------------------------------------------------------------------

var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419'

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-01-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource serviceBusRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, principalId, serviceBusDataOwnerRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// ----------------------------------------------------------------------------
// Cosmos DB Built-in Data Contributor — only when the active provider is Cosmos.
// SQL Server identity grants are issued by the database itself (CREATE USER FROM
// EXTERNAL PROVIDER + role membership) outside of this Bicep template.
// ----------------------------------------------------------------------------

var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2022-05-15' existing = if (storageProvider == 'cosmos') {
  name: cosmosAccountName
}

resource cosmosRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2022-05-15' = if (storageProvider == 'cosmos') {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, principalId, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: principalId
    scope: cosmosAccount.id
  }
}
