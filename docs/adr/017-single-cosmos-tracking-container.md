# ADR-017: One Cosmos DB Tracking Container, Partitioned by Endpoint

## Status
Proposed (2026-09). Supersedes [ADR-008](008-per-endpoint-cosmos-containers.md) when accepted.
The design, migration and evaluation are in
[Spec 036](../spec/036-cosmos-single-tracking-container/spec.md).

## Context

ADR-008 gave every endpoint its own Cosmos container for its tracking rows, partitioned by `/id`.
Each benefit it cited has either lapsed or can be had another way:

- **Isolated queries.** A query for one endpoint doesn't read other endpoints' data. A partition key
  that starts with the endpoint gives the same routing inside one container.
- **Throughput per endpoint.** Never used. Nothing sets throughput on an endpoint container, so each
  one carries the 400 RU/s minimum of a dedicated container, busy or idle.
- **Purge as a container delete.** The deployed apps authenticate with Entra data-plane RBAC, which
  can't create or delete containers. Purge and the lazy re-creation that follows it depend on
  account keys.
- **Single-endpoint copy.** The copy tools already use filtered queries.

Meanwhile its costs grew:

- Every endpoint needs a control-plane provisioning step (`nb topology apply`). Under managed
  identity, the first message on an unprovisioned endpoint fails.
- Endpoint ids double as container names: 13 reserved names, checked at five call sites.
- Throughput cost scales with the number of endpoints.
- An account holds at most 500 databases and containers, which caps the endpoint count.
- Containers were never an access boundary. Cosmos roles are assigned at account scope, and
  per-endpoint authorization lives in the WebApp.
- The SQL Server provider (ADR-010) already keeps every endpoint's rows in one table with an
  `EndpointId` column.

Options considered:

1. **Keep per-endpoint containers on shared database throughput.** Rejected. Microsoft advises
   against shared database throughput for most workloads, caps it at 25 containers, and an existing
   database can't switch. It also fixes none of the provisioning problems.
2. **Keep per-endpoint containers on a serverless account.** Rejected. A provisioned account can't
   be converted to serverless, and the provisioning problems remain.
3. **One container partitioned by `/endpointId`.** Rejected. It caps each endpoint at 20 GB and
   10,000 RU/s.
4. **One container partitioned by `/id`, with the endpoint folded into the row id.** Rejected. It
   changes ids the API exposes, and every endpoint query fans out to all partitions.
5. **One container with a hierarchical key `/endpointId`, `/sessionId`, `/id`.** Rejected. Some
   lookups receive only the composite row id, from which the session can't be recovered reliably.
6. **One container with a hierarchical key `/endpointId`, `/id`.** Chosen.

## Decision

- **One container.** `unresolvedevents` holds every endpoint's tracking rows. Bicep declares it
  with TTL on (`defaultTtl: -1`, item-level expiry, as before) and autoscale throughput. The name
  matches the SQL Server table.
- **Partition key.** The container uses the hierarchical key `/endpointId` then `/id`. Every row
  carries a top-level `endpointId`, which the store writes from the `endpointId` argument. Row ids
  (`{eventId}_{sessionId}`) are unchanged.
- **Endpoint-scoped access.** The store reaches the container only through an endpoint-scoped
  accessor. The accessor builds the full key for point operations and adds `c.endpointId =
  @endpointId` to every query. The predicate is equality, never a prefix match.
- **Purge.** Purging an endpoint deletes its rows through a paged query and paced deletes. Delete by
  partition key is in preview and accepts only full keys on hierarchical containers.
- **Provisioning.** `nb topology apply` and `nb setup` stop provisioning Cosmos containers.
  Endpoint ids no longer need to avoid container names.
- **Migration.** Existing Cosmos deployments migrate once, offline, in a major release, with
  `nb container migrate`. The command copies and verifies rows and never touches the legacy
  containers, which stay in place for rollback until an operator deletes them.
- **Out of scope.** The `messages` and `audits` containers, the other platform containers, the SQL
  Server and in-memory providers, the storage contracts and the WebApp API are unchanged.

## Consequences

### Positive
- New endpoints need no Cosmos provisioning. The first-message failure under managed identity and
  the reserved-name rules go away.
- Tracking throughput cost follows aggregate load. With 20 endpoints, a fixed ~$467 a month at the
  400 RU/s minimum becomes one autoscale container at ~$35 to ~$350 a month (US list prices).
- Idle endpoints' capacity is available to busy ones, and cross-endpoint queries read one container.
- Endpoint-scoped queries stay routed to the endpoint's partitions as the container grows, and no
  endpoint can hit the 20 GB logical partition limit.
- Purge works under data-plane RBAC.
- The Cosmos layout matches the SQL Server provider, so their conformance behaviour converges.
- The 500-resource account limit no longer caps the number of endpoints.

### Negative
- **Shared budget.** A burst on one endpoint, a large WebApp scan or a purge can throttle Resolver
  writes for every endpoint. Mitigations: autoscale, paced purges, and optionally priority-based
  execution.
- **Isolation in code.** Endpoint isolation depends on the scoped accessor. A unit test over every
  recorded query and cross-endpoint conformance tests guard it.
- **Lost options.** It gives up per-endpoint data-plane role assignments and resource tokens (unused
  today), per-endpoint throughput and TTL, and per-endpoint point-in-time restore.
- **Slower purge.** Purge costs RUs in proportion to the endpoint's rows and can stop part-way.
  Deleting a container was free and all-or-nothing.
- **Hot-endpoint ceiling.** One endpoint's throughput tops out at one physical partition's
  10,000 RU/s until its data splits. Before, the ceiling was its container's provisioned RU/s.
- **Emulator dependence.** CI depends on the emulator's hierarchical-key support, which is recent.
  Spec 036 Phase 0 verifies it before any code lands.
- **Migration outage.** Existing deployments take a planned outage for the migration.
- **Divergence from DIS.** Cosmos store changes from DIS, which keeps per-endpoint containers, no
  longer port mechanically.
