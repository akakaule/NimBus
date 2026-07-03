param serviceBusNamespaceName string
param cosmosAccountName string = ''
param principalId string

@allowed([
  'cosmos'
  'sqlserver'
])
param storageProvider string = 'cosmos'

// When the resolver runs on Flex Consumption, it needs identity-based access to
// the function-app storage account: the AzureWebJobsStorage host runtime AND the
// deployment package container (functionAppConfig.deployment.storage with
// SystemAssignedIdentity). Storage Blob Data Owner is the documented minimum for
// identity-based host storage — the host key store lives in blobs.
param funcStorageAccountName string = ''
param grantFuncStorageBlobAccess bool = false

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
// Provisioned SQL Server deployments use the SQL login created with the server;
// no Azure RBAC data-plane role assignment is available here.
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

// ----------------------------------------------------------------------------
// Storage Blob Data Owner on the function storage account — only when the
// resolver runs on Flex Consumption (which uses SystemAssignedIdentity for both
// AzureWebJobsStorage and the app-package container).
// ----------------------------------------------------------------------------

var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'

resource funcStorageAccount 'Microsoft.Storage/storageAccounts@2021-09-01' existing = if (grantFuncStorageBlobAccess) {
  name: funcStorageAccountName
}

resource funcStorageBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantFuncStorageBlobAccess) {
  name: guid(funcStorageAccount.id, principalId, storageBlobDataOwnerRoleId)
  scope: funcStorageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
