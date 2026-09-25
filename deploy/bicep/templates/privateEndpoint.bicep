// One private endpoint to a NimBus resource, optionally registered in private DNS
// zones through a zone group. Every NimBus private endpoint goes through this module
// so naming, ownership tags and DNS handling stay uniform (spec 034 §5.2).

param name string

// Must be the region of the private-endpoint subnet's virtual network, which can
// differ from the region of the resource the endpoint connects to.
param location string

param subnetId string

param privateLinkServiceId string

// Private Link sub-resource: namespace, Sql, sqlServer, blob, queue, table, file, sites.
param groupId string

// Zones the endpoint registers its A records in. Empty when privateDnsMode is
// 'external': the customer's policy or DNS servers create the records instead.
param privateDnsZoneIds array = []

// Carries the nimbus-deployment ownership tag the rollback cleanup looks for.
param tags object = {}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: privateLinkServiceId
          groupIds: [
            groupId
          ]
        }
      }
    ]
  }
}

resource zoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (!empty(privateDnsZoneIds)) {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [for zoneId in privateDnsZoneIds: {
      name: replace(last(split(zoneId, '/')), '.', '-')
      properties: {
        privateDnsZoneId: zoneId
      }
    }]
  }
}

output id string = privateEndpoint.id
