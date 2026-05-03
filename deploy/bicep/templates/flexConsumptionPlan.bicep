param name string
param location string = resourceGroup().location

// Flex Consumption requires:
// - kind: 'functionapp' (NOT 'elastic' which is used for Elastic Premium)
// - sku.tier: 'FlexConsumption', sku.name: 'FC1'
// - properties.reserved: true (Flex is Linux-only)
resource flexPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: name
  location: location
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

output id string = flexPlan.id
