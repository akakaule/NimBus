# ADR-016: Private Networking Is an Opt-In, Bring-Your-Own-Network Deployment Mode

## Status
Accepted (2026-09). Phase 1 of [Spec 034](../spec/034-private-networking/spec.md) implements
it; the how-to is [Private networking](../private-networking.md).

## Context

Security-focused customers require every NimBus component and data path to run inside their
virtual network. Until this change, every NimBus endpoint answered the public internet:

- Service Bus was Standard, which has no private endpoints.
- Azure SQL admitted every Azure tenant.
- Both apps and their Kudu sites were public.
- Deployments ran from public hosted runners.

The application code was largely ready. It reaches Service Bus by host name with managed
identity and Cosmos DB with Entra RBAC, and a private endpoint changes what a name resolves
to, not the name. The gap was the infrastructure and the deployment path.

Enterprises almost always own their network: hub-and-spoke VNets, DNS zones in a hub, and
Azure Policy that writes private DNS records and denies public access. Their constraints
shape the design more than NimBus's do.

Options considered:

1. **Private by default.** Rejected: it forces Service Bus Premium and an in-network runner on
   every deployment, including evaluation and development.
2. **NimBus creates and owns the VNet.** Rejected as the main path: it collides with customers'
   hub-and-spoke networks, IP planning and policy. A NimBus-created VNet stays a possible
   convenience for evaluation.
3. **Opt-in private mode with customer-owned subnets and pluggable DNS.** Chosen.
4. **Provision topology through Azure Resource Manager**, so public hosted runners still work.
   Rejected: it creates a second provisioning path next to `ServiceBusTopologyProvisioner`, and
   app deployment through Kudu would still need the private network.
5. **Keep Kudu public with IP restrictions.** Rejected: hosted-runner ranges are broad, so
   the requirement is not met.
6. **Standard Service Bus behind an IP firewall.** Rejected: the namespace stays on the public
   internet.
7. **App Service Environment, Container Apps or AKS.** Rejected: disproportionate cost or a
   rewrite, when multi-tenant plans already support private endpoints and VNet integration.

## Decision

- `--network-mode private` (Bicep `networkMode = 'private'`) is opt-in. Public mode deploys
  what NimBus always deployed and only declares today's defaults explicitly, so leaving
  private mode is deterministic.
- The customer supplies the subnets. NimBus validates their delegation and region but never
  modifies the VNet.
- The customer chooses DNS ownership: `create` (NimBus zones, for dedicated VNets, with fallback
  to internet on the links), `existing` (hub zones) or `external` (policy or custom DNS). There
  is no default.
- Private mode requires Service Bus Premium. Azure cannot convert a namespace between tiers,
  so an existing Standard namespace is refused, and a Premium namespace is deployed under a new
  name.
- Both apps route all outbound traffic through the VNet with `outboundVnetRouting.allTraffic`,
  not the legacy route-all flag, so managed-identity token requests pass the customer's
  firewall too.
- A deployment is in one of three states, `public`, `private-transition` or `private`. The CLI
  records the full network setup as resource-group tags before it deploys, and converges to it
  on every run.
- Existing deployments move in two passes, adding private paths while public access stays on
  and locking afterwards. A direct switch needs explicit acceptance of the downtime.
- Leaving private mode deletes only network resources tagged with the deployment's ownership
  tag, after public access is back.
- Deployments run from inside the network. The CLI checks DNS resolution of the private
  endpoints before each step that needs them.

## Consequences

- Existing public deployments are unaffected, and public users see no new tags, checks or
  failure modes.
- Private mode costs a Service Bus Premium messaging unit, 5–8 private endpoints and an
  in-network runner.
- The deploying identity needs rights outside the NimBus resource group: joining the customer's
  subnets, and writing to hub DNS zones in `existing` mode.
- Telemetry still uses public Azure Monitor endpoints until the monitoring phase adds Azure
  Monitor Private Link Scope support.
- SQL managed identity, local-auth-off and Key Vault are separate, later hardening.
- Live verification needs a sandbox subscription with a VNet and a runner inside it; CI covers
  the templates and the CLI logic only.
