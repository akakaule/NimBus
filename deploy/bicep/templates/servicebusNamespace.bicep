param name string
param location string = resourceGroup().location

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-01-01-preview' = {
  name: name
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}
output fullyQualifiedNamespace string = '${serviceBus.name}.servicebus.windows.net'
