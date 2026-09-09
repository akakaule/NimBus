targetScope = 'resourceGroup'

@description('All referenced Service Bus resources must be in this resource group. Provision the NimBus publisher topic before deployment.')
param nimbusNamespace string
param ingressNamespace string
param publisherTopic string = 'DataverseEndpoint'
param queueName string = 'dataverse-inbound'
param functionName string
param organizationId string
param location string = resourceGroup().location
@description('Dataverse table logical names mapped to explicit arrays of allowed column names.')
param tables tableProjections
@description('Enable only after validating source identity and registration in the designated test environment.')
param ingressEnabled bool = false

// An empty column array cannot be expressed as Dataverse__Tables__<table>__<index> app settings,
// so the table would silently vanish from the adapter's allowlist. Reject it at deployment instead.
@minLength(1)
type columnName = string

@minLength(1)
type columnProjection = columnName[]

type tableProjections = {
  *: columnProjection
}

var suffix = uniqueString(resourceGroup().id, functionName)
var storageName = 'dv${suffix}'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${functionName}-identity'
  location: location
}
resource ingress 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: ingressNamespace }
resource nimbus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: nimbusNamespace }
resource topic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' existing = {
  parent: nimbus
  name: publisherTopic
}
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: ingress
  name: queueName
  properties: { requiresSession: false, lockDuration: 'PT1M', maxDeliveryCount: 10, defaultMessageTimeToLive: 'P14D', deadLetteringOnMessageExpiration: true }
}
resource sendPolicy 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2024-01-01' = {
  parent: queue
  name: 'dataverse-send'
  properties: { rights: ['Send'] }
}
resource receiveRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(queue.id, identity.id, 'receiver')
  scope: queue
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e651ac7b2b8') }
}
resource sendRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(topic.id, identity.id, 'sender')
  scope: topic
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39') }
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: { allowBlobPublicAccess: false, allowSharedKeyAccess: false, minimumTlsVersion: 'TLS1_2', supportsHttpsTrafficOnly: true }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = { parent: storage, name: 'default' }
resource packageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'deployment'
  properties: { publicAccess: 'None' }
}
resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, 'host-storage')
  scope: storage
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b') }
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${functionName}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${functionName}-insights'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: workspace.id }
}
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${functionName}-plan'
  location: location
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  kind: 'functionapp'
  properties: { reserved: true }
}
var baseSettings = [
  { name: 'AzureWebJobsStorage__accountName', value: storage.name }
  { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
  { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
  { name: 'DataverseServiceBus__fullyQualifiedNamespace', value: '${ingress.name}.servicebus.windows.net' }
  { name: 'DataverseServiceBus__credential', value: 'managedidentity' }
  { name: 'DataverseServiceBus__clientId', value: identity.properties.clientId }
  { name: 'NimBusServiceBus__fullyQualifiedNamespace', value: '${nimbus.name}.servicebus.windows.net' }
  { name: 'DataverseQueue', value: queue.name }
  { name: 'Dataverse__OrganizationId', value: organizationId }
  { name: 'Dataverse__PublisherEndpoint', value: publisherTopic }
  { name: 'AzureWebJobs.DataverseIngress.Disabled', value: string(!ingressEnabled) }
]
var projectionSettings = flatten(map(items(tables), table => map(range(0, length(table.value)), index => {
  name: 'Dataverse__Tables__${table.key}__${index}'
  value: string(table.value[index])
})))
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: { minTlsVersion: '1.2', ftpsState: 'Disabled', appSettings: concat(baseSettings, projectionSettings) }
    functionAppConfig: {
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${packageContainer.name}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: identity.id }
        }
      }
    }
  }
  dependsOn: [storageRole, receiveRole, sendRole]
}
output functionAppName string = app.name
output ingressQueueName string = queue.name
output dataverseAuthorizationRule string = sendPolicy.name
