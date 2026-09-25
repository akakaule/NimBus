// Private DNS zones for privateDnsMode 'create' (spec 034 §5.3): NimBus creates each
// zone it needs once and links it to the given virtual networks.
//
// A linked zone answers for every name of its type resolved from that VNet, so a zone
// without a record would hide other private endpoints of the same type there. Every
// link therefore falls back to public resolution on NXDOMAIN. Shared or hub-connected
// VNets should still use 'existing' or 'external' instead of this mode.

param zoneNames array

param vnetIds array

param tags object = {}

resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for zoneName in zoneNames: {
  name: zoneName
  location: 'global'
  tags: tags
}]

// One link per zone and VNet. The link name is derived from the VNet id so re-runs
// address the same link.
var links = flatten(map(range(0, length(zoneNames)), zoneIndex => map(vnetIds, vnetId => {
  zoneIndex: zoneIndex
  vnetId: vnetId
})))

resource vnetLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for link in links: {
  parent: zones[link.zoneIndex]
  name: 'nimbus-${uniqueString(link.vnetId)}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    resolutionPolicy: 'NxDomainRedirect'
    virtualNetwork: {
      id: link.vnetId
    }
  }
}]
