param name string
param location string = resourceGroup().location

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-01-01-preview' = {
  name: name
  location: location
}
var listKeysEndpoint = '${serviceBus.id}/AuthorizationRules/RootManageSharedAccessKey'
output SharedAccessKey string = 'Endpoint=sb://${serviceBus.name}.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=${listKeys(listKeysEndpoint, serviceBus.apiVersion).primaryKey}'
