param name string
param location string = resourceGroup().location

// Standard stays the default. Private networking needs Premium: private endpoints and
// VNet rules exist only on that tier, and Azure cannot convert a namespace between
// tiers in place (spec 034 §5.4).
@allowed([
  'Standard'
  'Premium'
])
param sku string = 'Standard'

// Premium messaging units.
@allowed([
  1
  2
  4
  8
  16
])
param capacity int = 1

// Premium only; a Standard namespace has no network controls and ignores it.
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

var isPremium = sku == 'Premium'

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  sku: isPremium ? {
    name: 'Premium'
    tier: 'Premium'
    capacity: capacity
  } : {
    name: 'Standard'
    tier: 'Standard'
  }
  // Non-partitioned: ServiceBusTopologyProvisioner never sets EnablePartitioning,
  // and Microsoft's Standard-to-Premium migration requires a non-partitioned target.
  properties: isPremium ? {
    premiumMessagingPartitions: 1
    minimumTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
  } : {}
}

resource networkRules 'Microsoft.ServiceBus/namespaces/networkRuleSets@2024-01-01' = if (isPremium) {
  parent: serviceBus
  name: 'default'
  properties: {
    publicNetworkAccess: publicNetworkAccess
    defaultAction: publicNetworkAccess == 'Disabled' ? 'Deny' : 'Allow'
    trustedServiceAccessEnabled: false
    ipRules: []
    virtualNetworkRules: []
  }
}

output id string = serviceBus.id
output fullyQualifiedNamespace string = '${serviceBus.name}.servicebus.windows.net'
