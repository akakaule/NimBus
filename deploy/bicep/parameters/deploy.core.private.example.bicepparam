// Sample parameters for a private-network deployment of the NimBus core
// infrastructure (spec 034). Every NimBus endpoint gets a private endpoint in a
// customer-owned subnet, and public network access is disabled.
// Deploy with (Azure CLI >= 2.53.0, Bicep CLI >= 0.30):
//   az deployment group create \
//     --resource-group rg-nimbus-prod \
//     --template-file ../deploy.core.bicep \
//     --parameters deploy.core.private.example.bicepparam
// Existing public deployments must pass through allowPublicAccess = true first
// (the private-transition state); see docs/spec/034-private-networking/spec.md §6.
using '../deploy.core.bicep'

param solutionId = 'nimbus'
param environment = 'prod'
param resolverId = 'Resolver'
param uniqueDeploy = 'manual-prod'
param locationParam = 'westeurope'

param resolverPlan = 'FlexConsumption' // preferred for private mode: identity-based storage, no content share
param storageProvider = 'cosmos'

param networkMode = 'private'
param allowPublicAccess = false

// Customer-owned subnets. The resolver subnet must be delegated to
// Microsoft.App/environments (Flex) or Microsoft.Web/serverFarms (Elastic Premium).
param privateEndpointSubnetId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network/providers/Microsoft.Network/virtualNetworks/vnet-nimbus/subnets/snet-private-endpoints'
param resolverSubnetId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network/providers/Microsoft.Network/virtualNetworks/vnet-nimbus/subnets/snet-resolver'
param privateEndpointLocation = 'westeurope'

// 'existing': the privatelink zones live in the hub's DNS resource group.
param privateDnsMode = 'existing'
param privateDnsZoneScope = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-hub-dns'

param serviceBusCapacity = 1
