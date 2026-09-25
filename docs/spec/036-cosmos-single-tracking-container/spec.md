# Spec 036 — One Cosmos DB tracking container for all endpoints

Status: **proposed** (2026-09-26). Nothing is implemented. Decision record:
[ADR-017](../../adr/017-single-cosmos-tracking-container.md) (proposed), which supersedes ADR-008
when accepted.
Baseline: master `f16a0369`.
Scope: `NimBus.MessageStore.CosmosDb`, the `nb` CLI (`topology apply`, `setup`, `container copy`
and a new `container migrate`), the WebApp's Cosmos admin paths (purge, copy, storage containers),
`deploy/bicep/templates/cosmosDB.bicep` and docs. Unchanged: the storage contracts in
`NimBus.MessageStore.Abstractions`, the SQL Server and in-memory providers, the `messages` and
`audits` containers, the Service Bus topology and the message flow.
Why: the per-endpoint layout (ADR-008) costs each endpoint a fixed minimum throughput. It needs a
control-plane provisioning step per endpoint, which managed identity can't do at runtime, and it
couples endpoint ids to container names. None of the benefits ADR-008 cites is in use today (§2.2).

## 1. Summary

Today every endpoint has its own Cosmos container for its tracking rows (the unresolved, failed,
deferred and completed projections the Resolver writes). This spec replaces those containers with
one container, `unresolvedevents`:

- **Partitioning.** A hierarchical partition key, `/endpointId` then `/id`. Endpoint-scoped queries
  stay routed to the endpoint's own partitions. Every row stays its own logical partition, so no
  endpoint can reach the 20 GB logical partition limit.
- **Isolation by construction.** An internal `EndpointScope` is the only way the store reaches the
  container. It builds the full partition key for point operations and adds
  `c.endpointId = @endpointId` to every query.
- **Provisioning.** Bicep declares the container once. Adding an endpoint no longer touches Cosmos.
- **Purge.** Purging an endpoint deletes its rows instead of deleting a container.
- **Migration.** Existing deployments migrate once and offline with `nb container migrate`, in a
  major release. The legacy containers stay in place for rollback until an operator deletes them.

The main costs are a shared throughput budget (noisy neighbours), isolation that now depends on
code rather than on container boundaries, a purge that costs request units (RUs), a young
emulator feature and a short migration outage. §7 weighs them.

## 2. Current state

### 2.1 Facts

| Aspect | Today |
|---|---|
| Layout | One container per endpoint in `MessageDatabase`. The container id is the endpoint id (ADR-008) |
| Partition key | `/id` (`CosmosContainerDefaults.EndpointPartitionKeyPath`, `CosmosContainerDefaults.cs:15`). The row id is `{eventId}_{sessionId}`, so every row is its own logical partition |
| TTL | Container TTL on, no default (`-1`). Rows carry item TTLs: 30 days for terminal and archived rows, 60 s for soft-deleted rows, `UnresolvedRetentionDays` for unresolved rows |
| Throughput | Never set: not in Bicep, not by `EndpointContainerProvisioner`, not by the SDK calls. A container with dedicated manual throughput has a 400 RU/s minimum (§14 item 1) |
| Creation | (a) `nb topology apply` and `nb setup` call `EndpointContainerProvisioner` (`TopologyCommands.cs:121`, `SetupCommand.cs:137`), which runs `az cosmosdb sql container create`. (b) `CosmosDbClient.GetEndpointContainer` (`CosmosDbClient.cs:203`) creates the container lazily. This works only with account keys, because data-plane RBAC can't create containers; under managed identity the first message on an unprovisioned endpoint fails with 403. (c) `nb container copy` and the WebApp's Copy Endpoint Data create the container in the target account |
| Naming rules | An endpoint id may not equal one of the 13 reserved container ids (`CosmosContainerDefaults.ReservedContainerIds`, `CosmosContainerDefaults.cs:31`). Five call sites check this |
| Access | The Resolver and WebApp identities hold Cosmos DB Built-in Data Contributor at account scope (`roleAssignments.bicep:63-67`). Nothing is granted per container |
| Endpoint purge | Deletes the endpoint container, then drops the cached handle so the next access re-creates it (`CosmosDbMessageTrackingStore.Writes.cs:229`). Two WebApp entry points call it: the endpoint page's purge, refused in Production and Staging (`EndpointImplementation.cs:497`), and Admin → Delete all events, which requires a site Owner and works in every environment (`AdminImplementation.cs:223`) |
| Cross-endpoint reads | The failed-messages page and the histogram query each endpoint container, eight at a time, and merge the results (`CosmosDbMessageTrackingStore.Search.cs:194`) |
| Storage admin | Admin → Storage containers lets a site Owner delete any container that is neither reserved nor named after a catalog endpoint (`AdminImplementation.cs:317`). It deletes through ARM when `CosmosAccountResourceId` is set (`ArmCosmosContainerAdmin`) |
| SQL Server provider | One `UnresolvedEvents` table with an `EndpointId` column and indexes `(EndpointId, Status)`, `(EndpointId, SessionId, Status)` and `(EndpointId, UpdatedAtUtc DESC)` (`Schema/0003_Events.sql:37-39`) |
| Upstream | DIS, which NimBus is forked from, keeps per-endpoint containers (`BH.DIS.MessageStore/CosmosDbClient.cs`) |

### 2.2 What ADR-008's benefits amount to today

| ADR-008 benefit | Today |
|---|---|
| Queries for one endpoint don't scan other endpoints' data | True. A partition key that starts with the endpoint gives the same routing inside one container (§5.1) |
| Throughput can be provisioned per endpoint | Never used. No code path or template sets throughput on an endpoint container, so each one carries the same minimum whether it is busy or idle |
| Purge is a container delete | The deployed apps authenticate with data-plane RBAC. Deleting and lazily re-creating a container are management operations that data-plane RBAC doesn't permit, so this path most likely works only with account keys (§14 item 6) |
| Copy can target one endpoint without filtering | The copy tools already build filtered queries (`AdminService.Copy.cs`, `Container.cs:77`), so a filter on `endpointId` is one more condition |

## 3. Goals

1. One tracking container for all endpoints, declared statically. Adding, renaming or removing an
   endpoint never creates or deletes Cosmos resources.
2. Every `IMessageTrackingStore` member keeps its observable behaviour. The conformance suite pins
   this, and new cross-endpoint isolation tests extend it (§8).
3. Endpoint isolation is enforced by one code path that callers can't bypass, not by convention.
4. Endpoint-scoped reads stay targeted as the container grows, with no full fan-out.
5. Existing Cosmos deployments get an idempotent, verifiable migration with a rollback path.
6. Tracking throughput cost follows aggregate load, not endpoint count.

## 4. Non-goals

- The other platform containers (`messages`, `audits`, `subscriptions`, `Metadata` and the rest).
  Consolidating the small ones is a possible follow-up, not part of this change.
- Shared database throughput or a serverless account. Microsoft recommends container-level
  throughput for most workloads, and an existing account can't be converted from provisioned to
  serverless (§12).
- The SQL Server and in-memory providers. They already use one store per concern.
- The row id format `{eventId}_{sessionId}`. It appears in API responses and URLs.
- A zero-downtime migration through the change feed (§12).
- Throughput or TTL tuned per endpoint.

## 5. Design

### 5.1 The container

| Property | Value |
|---|---|
| Id | `unresolvedevents`, in `MessageDatabase`. It mirrors the SQL Server table name |
| Partition key | Hierarchical: `["/endpointId", "/id"]`, kind `MultiHash`, version 2 |
| TTL | `defaultTtl: -1`, which turns TTL on with item-level expiry, exactly as endpoint containers have it today |
| Throughput | Autoscale. The max comes from a new Bicep parameter, surfaced by `nb infra apply` the same way as the existing plan and SKU options. Proposed default: 4,000 RU/s (§13 item 3) |
| Indexing | The default policy (all paths) plus composite indexes that mirror the SQL Server provider: `(endpointId, status)`, `(endpointId, sessionId, status)` and `(endpointId, event.UpdatedAt DESC)`. Phase 0 measures the final set on a live account, because the emulator ignores custom indexing policies (§14 item 5) |

Why `id` is the second level:

- **No signature changes.** Every point operation already has both values: the `endpointId`
  argument and the row id.
- **Targeted reads.** A query whose `WHERE` clause pins the first level runs only against the
  physical partitions that hold that prefix
  ([hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys)).
- **No 20 GB ceiling.** With `id` last, every logical partition holds one row, so no endpoint can
  reach the per-logical-partition limit. Microsoft recommends the item id as the last level for
  this reason.
- **Same row semantics.** Row ids only need to be unique per full key. The same
  `{eventId}_{sessionId}` on two endpoints stays two rows, as it is today when one event reaches
  several subscribers.

Three levels (`/endpointId`, `/sessionId`, `/id`) were rejected (§12).

### 5.2 Document changes

- `EventDbo` gains `[JsonProperty("endpointId")] string EndpointId`. The store sets it from the
  `endpointId` argument of every write, which is the value that selects the container today. It
  never takes it from `UnresolvedEvent.EndpointId`.
- The partition key is `new PartitionKeyBuilder().Add(endpointId).Add(id).Build()`.
- The row id, the embedded `event` document and the server-side projections (`ProjectForSearch` and
  the paging projection) are unchanged. The projections already carry `event.EndpointId`.

### 5.3 `EndpointScope`: the only way into the container

```csharp
internal sealed class EndpointScope
{
    public string EndpointId { get; }

    // Point operations take row ids and build the full key (EndpointId, id) themselves.
    public Task<ItemResponse<EventDbo>> ReadAsync(string id);
    public Task<ItemResponse<EventDbo>> CreateAsync(EventDbo row);               // stamps EndpointId
    public Task<ItemResponse<EventDbo>> UpsertAsync(EventDbo row, ItemRequestOptions? options = null);
    public Task<ItemResponse<EventDbo>> ReplaceAsync(EventDbo row, ItemRequestOptions options);
    public Task<ItemResponse<EventDbo>> PatchAsync(string id, IReadOnlyList<PatchOperation> operations);
    public Task<ItemResponse<EventDbo>> DeleteAsync(string id);
    public Task<FeedResponse<EventDbo>> ReadManyAsync(IReadOnlyList<string> ids);

    // Queries: the scope owns the WHERE clause's first conjunct.
    public QueryDefinition Query(string select, string? where = null, string? orderBy = null);
    public IQueryable<EventDbo> Rows(string? continuationToken = null, QueryRequestOptions? options = null);
}
```

- `CosmosDbMessageTrackingStore` takes `Func<string, Task<EndpointScope>>` instead of
  `Func<string, Task<ICosmosContainerAdapter>>` (`CosmosDbMessageTrackingStore.cs:20`). Its method
  bodies never see the raw container.
- `Query` emits `SELECT {select} FROM c WHERE c.endpointId = @__endpointId [AND ({where})]
  [ORDER BY {orderBy}]`. `Rows` returns the LINQ queryable with
  `.Where(x => x.EndpointId == EndpointId)` already applied.
- The predicate is **equality**, never `STARTSWITH`. `Billing` must not match `BillingV2`, the same
  trap that `SearchAudits_EndpointIdExact_excludes_prefix_siblings` pins for audits.
- The predicate also routes the query. Microsoft's guidance is to put prefix values in the
  `WHERE` clause, because supplying them only through `PartitionKeyBuilder` doesn't guarantee
  efficient routing.
- If Phase 0 shows that a prefix `QueryRequestOptions.PartitionKey` also restricts results, the
  scope sets it as a second, server-side fence (§14 item 4).
- The endpoint id must be non-empty, as today. The reserved-name check is no longer needed.

### 5.4 Operation by operation

| Store members | Today | Proposed |
|---|---|---|
| `GetPendingEvent`, `GetFailedEvent`, `GetDeferredEvent`, `GetDeadletteredEvent`, `GetUnsupportedEvent`, `GetEventById` | Point read, key `id` | Point read, key `(endpointId, id)` |
| `UploadPendingMessage`, `UploadDeferredMessage` (guarded writes, Spec 030) | Read, then create or ETag-conditional upsert | The same with the full key. The compare-and-swap is unchanged |
| `UploadFailedMessage`, `UploadDeadletteredMessage`, `UploadUnsupportedMessage`, `UploadCompletedMessage`, `UploadSkippedMessage` | Upsert | Upsert with `endpointId` stamped |
| `TrySkipDeferredMessage`, `TryCompletePendingMessage` | Read, then conditional replace or upsert | The same with the full key |
| `RemoveMessage`, `ArchiveFailedEvent` | Patch | Patch with the full key |
| `GetEventsByIds` | `ReadManyItemsAsync` with `(id, key(id))` pairs | `(id, key(endpointId, id))` pairs |
| `DownloadEndpointStateCount`, `DownloadEndpointStatePaging`, `GetEventsByFilter`, `GetCompletedEventsOnEndpoint`, `GetEndpointErrorList`, `GetInvalidEventsOnSession`, `GetPendingEventsOnSession`, `GetEvent(endpointId, eventId)`, `GetPendingHandoffByExternalJobId`, `GetNextPendingHandoffEvent` | Query over the whole endpoint container | The same query with the endpoint predicate, routed by prefix |
| `DownloadEndpointSessionStateCount`, `DownloadEndpointSessionStateCountBatch`, `GetBlockedEventsOnSession`, `PurgeMessages(endpointId, sessionId)` | Query by `sessionId` | The same with the endpoint predicate |
| `GetFailedEventsAcrossEndpoints`, `GetFailedEventHistogram` | One query per endpoint container, merged by `FailedEventPageCursor` | One prefix query per endpoint against the same container. The cursor is unchanged. Collapsing them into one query is a follow-up (§13 item 7) |
| `PurgeMessages(endpointId)` | Deletes the container | §5.5 |

Continuation tokens issued before the cutover are invalid afterwards, so open UI pages reload
their lists once.

### 5.5 Purging an endpoint

`PurgeMessages(endpointId)` pages `SELECT c.id FROM c WHERE c.endpointId = @e` and deletes each
row with bounded parallelism. The session purge already does this (`Writes.cs:192`). The endpoint
purge uses a lower default parallelism than the session purge's 8 (proposed: 2), so it can't
saturate the budget the Resolver writes against. The method returns `false` on any failure, as its
contract says today, and a rerun deletes whatever is left.

- **Delete by partition key isn't an option.** It is in public preview and needs the
  `DeleteAllItemsByPartitionKey` account capability. On hierarchical containers it accepts only the
  full key; a prefix delete errors
  ([delete by partition key](https://learn.microsoft.com/azure/cosmos-db/how-to-delete-by-partition-key)).
  Here that would delete one row per call.
- **Cost.** Each delete is charged like a write and draws on the shared budget. With
  priority-based execution enabled, purge requests run at low priority (§13 item 6). The endpoint
  page's purge stays unavailable in Production and Staging. Admin → Delete all events stays
  available to site Owners everywhere; its confirmation should now say that the operation takes
  time and consumes throughput in proportion to the endpoint's rows.
- **Behaviour change.** Deleting a container was all-or-nothing. The new purge can stop part-way;
  it reports failure, and a rerun finishes the job.
- **Side benefit.** Purge no longer needs container-management rights, so it works under
  data-plane RBAC (§2.2).

### 5.6 Provisioning and runtime resolution

- **Bicep.** Declare `unresolvedevents` beside `messages` and `audits` in `cosmosDB.bicep`. The
  container API version the template already uses (`2021-06-15`) lists `MultiHash` in its schema;
  §14 item 3 confirms a real deployment.
- **CLI.** `nb topology apply` and `nb setup` stop provisioning Cosmos containers, and
  `EndpointContainerProvisioner` is deleted. The `--storage-provider` option of `nb topology apply`
  existed only for that step. It is still accepted for one major, with a deprecation warning, and
  then removed.
- **Runtime.** `CosmosDbClient` resolves `unresolvedevents` through `GetCachedContainerAsync`, like
  every other platform container. Lazy creation still serves key-authenticated development and the
  emulator. `GetEndpointContainer`, its reserved-id rejection and the `_removeEndpointContainerCache`
  callback go away.
- **Reserved ids.** `CosmosContainerDefaults.ReservedContainerIds` gains `unresolvedevents`.
- **Bicep sync test.** `CosmosBicepContainerSyncTests` (#152) requires every reserved id to be
  declared in `cosmosDB.bicep` with the store's partition key. It parses only single-path keys, so
  it has to learn the `MultiHash` path list and expect `["/endpointId", "/id"]` for
  `unresolvedevents`. The Bicep comment saying per-endpoint containers can't be declared statically
  goes as well.
- **Adapter caveat.** The default `ICosmosDatabaseAdapter.CreateContainerIfNotExistsAsync(ContainerProperties)`
  implementation in `CosmosAbstractions.cs` falls back to the single `PartitionKeyPath`. Every
  adapter that creates the tracking container must forward the full `ContainerProperties`, which
  carries `PartitionKeyPaths`. The production adapters already do; the test fakes must too.

### 5.7 Copy Endpoint Data (WebApp and `nb container copy`)

- `AdminService.CopyEndpointDataAsync` (`AdminService.Copy.cs:17`) and `Container.CopyEndpointData`
  (`Container.cs:77`) add `c.endpointId = @endpointId` to the existing event query. They read from
  the source's `unresolvedevents` and write with the full key to the target's.
- Both accounts must use the new layout. The tools check the target container's partition key
  definition and refuse otherwise. Moving data from a legacy account is the job of
  `nb container migrate` (§6).
- Copying `messages` is unchanged.

### 5.8 Admin → Storage containers

- `IsProtectedContainer` (`AdminImplementation.cs:317`) keeps protecting reserved ids, which now
  include `unresolvedevents`.
- It stops protecting containers merely because they are named after a catalog endpoint, with one
  exception: a legacy endpoint container stays protected until the migration marker (§6.1) lists it
  as migrated.
- Once migrated, legacy containers show as outside the platform, and the existing audited delete
  removes them. That is the cleanup path after a migration.
- The WebApp's Cosmos DB Operator role exists for this page. When the legacy containers are gone,
  orphaned endpoint containers can no longer appear. Retiring the page and the role is §13 item 5.

### 5.9 Public API and configuration

- `CosmosContainerDefaults` (public) gains `TrackingContainerId`, `TrackingPartitionKeyPaths` and
  `TrackingContainer()`.
- `EndpointPartitionKeyPath`, `EndpointContainer(string)` and `EnsureNotReservedEndpointId(string)`
  become `[Obsolete]`, with their current behaviour as the bridge, and are removed in the following
  major ([versioning](../../versioning.md)). `EndpointContainerDefaultTimeToLive` keeps its value
  and also serves the tracking container.
- The migration logic is a public, documented type in `NimBus.MessageStore.CosmosDb` (§6.1).
- Unchanged: `IMessageTrackingStore` and every other storage contract, `api-spec.yaml`, the
  configuration keys and `CosmosDbMessageStoreOptions`.

## 6. Migration

### 6.1 `nb container migrate`

The command takes the same connection options as the other `nb container` commands, plus
`--dry-run`, `--endpoint <id>` (repeatable; default: every legacy endpoint container),
`--parallelism <n>` and `--yes`.

1. **Preflight.** The command refuses to run in any of these cases:
   - `unresolvedevents` is missing, its partition key isn't `["/endpointId", "/id"]` `MultiHash`, or
     its TTL is off. The command never creates it; `nb infra apply` does.
   - Any selected legacy container was written in the last 5 minutes (`SELECT VALUE MAX(c._ts)`),
     unless `--force` is given. A copy taken while the Resolver or the WebApp still writes would
     silently miss later status changes.
2. **Selection.** Legacy endpoint containers are the containers in `MessageDatabase` that:
   - aren't in `ReservedContainerIds`;
   - aren't known extension containers (`intelligencesettings`, `failureclassifications`);
   - have the single partition key path `/id`.

   The command prints the list and asks for confirmation unless `--yes` is given.
3. **Copy.** For each container it streams `SELECT * FROM c` and transforms each row:
   - drops the system properties (`_rid`, `_self`, `_etag`, `_attachments`, `_ts`);
   - sets `endpointId` to the container id;
   - converts a positive `ttl` into the remaining lifetime, `ttl - (now - _ts)`, and skips rows
     whose TTL has already elapsed but which Cosmos hasn't deleted yet. It keeps `-1` and a missing
     `ttl` as they are;
   - creates the row with the full key. `409 Conflict` counts as already migrated, which makes
     reruns idempotent.

   After each container it re-reads `MAX(c._ts)`. If that changed, a writer was active, and the
   command fails that endpoint.
4. **Verify.** For each endpoint, the source row count (minus elapsed rows) must equal created plus
   already-present rows, and the per-status counts must match between source and target.
5. **Mark.** The command writes `settings/tracking-layout` as
   `{ "layout": "unresolvedevents", "migratedAtUtc": …, "endpoints": { "<id>": <rows> } }`.
6. **Never touches the source.** It doesn't modify or delete legacy containers.

Implementation notes:

- The command doesn't use the SDK's bulk mode. The vNext emulator doesn't support .NET bulk
  execution, so the command uses bounded parallel creates.
- The copy and verify logic lives in `NimBus.MessageStore.CosmosDb`, and `nb` only wires the
  options. That puts its emulator tests in `NimBus.MessageStore.CosmosDb.Tests`, under CI's
  "Cosmos DB conformance must not skip" gate.
- In private networking mode (Spec 034), run the command from inside the network, as with every
  other data-plane operation.

### 6.2 Cutover runbook

1. Read the release notes. Confirm that no endpoint is named `unresolvedevents`.
2. Upgrade `nb` and run `nb infra apply`. It creates `unresolvedevents` and leaves the existing
   containers alone. The old apps keep running.
3. Stop the Resolver Function App and the WebApp. Service Bus holds Resolver-bound messages in the
   Resolver subscription, within the message TTL. Adapters keep processing their own subscriptions.
4. Run `nb container migrate --dry-run`, then `nb container migrate`.
5. Deploy the new version with `nb deploy apps` and start both apps. The Resolver drains its
   backlog.
6. Compare the Monitor counts with the migration report.
7. After a soak period, delete the legacy containers from Admin → Storage containers.

External readers of endpoint containers must switch to `unresolvedevents` and filter on
`endpointId`. That includes change-feed consumers, saved Data Explorer queries and ops tools such
as the Cosmos repair tool Spec 032 used as its reference implementation.

### 6.3 Rollback

- **Before step 5:** start the old apps. Nothing changed for them.
- **After step 5:** redeploy the old apps. They read the legacy containers, which show the state
  at cutover; rows written since then exist only in `unresolvedevents`. `messages` and `audits`
  hold the full history either way. No reverse migration ships.

### 6.4 Downtime

Downtime ≈ rows ÷ (available RU/s ÷ RU per create). Worked example (every input is an assumption
until Phase 0 measures it): 1,000,000 rows at about 10 RU per create, with the autoscale max raised
to 10,000 RU/s for the run, is about 1,000 creates per second, or roughly 17 minutes. Most rows in
a busy deployment are terminal (Completed or Skipped, 30-day TTL). §13 item 4 offers a shorter
outage: copy only non-terminal rows during the outage and backfill terminal rows after the start.
Because the backfill only creates, rows the Resolver has written since always win.

## 7. Evaluation

Prices below are US list prices for a single write region: $0.008 per 100 RU/s-hour for manual
throughput and $0.012 for autoscale
([understanding your bill](https://learn.microsoft.com/azure/cosmos-db/understand-your-bill)).
400 RU/s of manual throughput costs about $23 a month.

### 7.1 Cost

| Endpoints | Tracking throughput today (N × 400 RU/s manual) | Proposed (`unresolvedevents`, autoscale max 4,000) |
|---|---|---|
| 5 | ~$117/month | ~$35/month idle, up to ~$350/month at max |
| 20 | ~$467/month | same range |
| 50 | ~$1,168/month | same range |

- **Fixed cost stops growing with endpoints.** Today every endpoint adds a fixed ~$23 a month for
  capacity it rarely uses. Autoscale bills the highest RU/s reached in each hour, and at least 10% of
  the max, so the proposed bill follows aggregate load.
- **Small deployments.** A five-endpoint deployment that runs near the autoscale max most of the
  time would pay more than today, but it would be getting ten times the capacity. The Bicep
  parameter lets it choose a max of 1,000 instead: $8.76 to $87.60 a month.
- **Unchanged.** The twelve platform containers, about $280 a month if each sits at the 400 RU/s
  minimum.
- **Not the lever.** Shared database throughput is the other obvious saving. Microsoft advises
  against it for most workloads, limits it to 25 containers and can't apply it to an existing
  database ([throughput](https://learn.microsoft.com/azure/cosmos-db/set-throughput)).

### 7.2 Performance

**Gains:**

- **Point operations.** These carry most of the Resolver's traffic: the guarded upsert pair,
  terminal upserts and patches. They cost the same RUs; the row gains one short property.
- **First message on a new endpoint.** Today it waits for a lazy container creation, or fails with
  403 under managed identity. Now there is nothing to create.
- **Pooled capacity.** Idle endpoints' capacity is available to busy ones, and autoscale absorbs
  bursts. Today a busy endpoint throttles at its own 400 RU/s even when every other endpoint is
  idle.
- **Cross-endpoint queries.** They hit one container instead of N, so the per-container overhead
  goes away. Collapsing them into a single query is a follow-up.

**Neutral, pending measurement:**

- **Endpoint-scoped queries.** Today they run against a small container of their own. Proposed,
  they are prefix-routed to the endpoint's partitions and filtered by an indexed equality. While the
  container is one physical partition (up to 50 GB and 10,000 RU/s), the RU cost should be
  comparable, and the composite indexes target the `GROUP BY` and `ORDER BY` queries. The emulator
  reports no RU charges, so Phase 0 measures this on a live account (§14 item 5).

**Losses:**

- **Noisy neighbour.** A burst on one endpoint, or an expensive WebApp scan, can throttle Resolver
  writes for every endpoint. Throttling makes the Resolver call `ScheduleRedelivery`, the reorder
  path behind the Spec 030 incident. Spec 030's guard makes the stale copies harmless, but every
  endpoint is delayed. Mitigations:
  - autoscale;
  - priority-based execution, with WebApp reads, purge and migration at low priority (§13 item 6);
  - Spec 031's capacity controls. Spec 031's capacity visibility also gets simpler: one container's
    metrics instead of N.
- **Hot-endpoint ceiling.** With tens of endpoints, the first key level has low cardinality. One
  endpoint's rows share a physical partition until splits spread the prefix, so one endpoint tops
  out at that partition's 10,000 RU/s. Today an endpoint's ceiling is its container's 400 RU/s, so
  this isn't a regression in practice, but it is a hard ceiling to document.

### 7.3 Security

- **The boundary doesn't move.** Cosmos access is granted at account scope today, so the containers
  never isolated one endpoint's data from an identity. Per-endpoint authorization (Spec 026 roles)
  is enforced by the WebApp, before any query runs.
- **What changes.** Isolation now rests on the partition-key prefix and the query predicate.
  `EndpointScope` makes that the only path. Two kinds of tests pin it: a unit test that inspects
  every recorded query, and conformance tests for cross-endpoint isolation (§8). The equality
  predicate rules out prefix-sibling leaks.
- **What's lost.** Two future options, neither used today:
  - granting an identity data-plane access to one endpoint's container (role assignments can be
    scoped to `/dbs/<db>/colls/<container>`);
  - issuing per-endpoint resource tokens, which can't target a partial hierarchical key.

  A future feature that reads one endpoint's rows directly would go through the WebApp API instead.
- **What improves:**
  - The runtime no longer needs container management rights for tracking.
  - Purge works under data-plane RBAC.
  - Endpoint ids no longer double as resource names.
  - `EndpointScope` bounds a bug in a bulk operation to one endpoint.
- **Unchanged.** Payloads, PII handling and the audit trail are identical.
- **Restore granularity.** Point-in-time restore works per container. Restoring one endpoint means
  restoring `unresolvedevents` to a new account and copying that endpoint's rows back with Copy
  Endpoint Data.

### 7.4 Maintainability and operations

**Removed:**

- `EndpointContainerProvisioner` and `EndpointContainerProvisionerTests`, and the provisioning step
  in `nb topology apply` and `nb setup`;
- `GetEndpointContainer`, the reserved-endpoint-id checks at five call sites and the
  container-cache eviction on purge;
- the per-container TTL mode juggling;
- two operator procedures in `docs/storage-providers.md`: turning on TTL for old endpoint containers,
  and backfilling each container.

**Added:**

- one Bicep resource;
- `EndpointScope`;
- the migration command, kept at least one major so late upgraders can migrate;
- tests.

**Other effects:**

- **Provider parity.** The Cosmos layout matches the SQL Server provider's `UnresolvedEvents` table,
  so the conformance behaviour of the two converges.
- **Operations.** New endpoints need no Cosmos step. The Cosmos 500-resource limit per account no
  longer caps the endpoint count, and there is one throughput dial and one set of metrics to watch.
- **Costs:**
  - Cosmos store changes ported from DIS stop applying mechanically.
  - CI now depends on the emulator's hierarchical-key support. The Linux vNext emulator listed it
    at GA on 2026-06-02, and a creation bug (#346) was fixed in 2026-08 (§14 item 2).
  - Per-endpoint throughput and TTL tuning, never used, can't be added later without a new
    container.

### 7.5 Scale limits

| Limit | Per-endpoint layout | One container |
|---|---|---|
| Endpoints per account | ~485: 500 databases plus containers, minus the database and platform containers. Can't be increased | No Cosmos limit |
| Data per endpoint | 20 GB per logical partition doesn't bind (`/id` key) | Doesn't bind (`/id` is the last level) |
| Throughput per endpoint | The container's provisioned RU/s (400 by default) | ~10,000 RU/s until the endpoint's prefix spans partitions |

## 8. Tests

**Unit, in `tests/NimBus.MessageStore.CosmosDb.Tests`, extending `RecordingCosmosAdapters`:**

- Every point operation passes the key `(endpointId, id)`.
- Every query the tracking store issues carries `c.endpointId = @…` bound to the scope's endpoint.
  LINQ queries are checked through `ToQueryDefinition()`. No query uses `STARTSWITH` on
  `endpointId`.
- Written rows carry `endpointId` equal to the argument, even when `UnresolvedEvent.EndpointId`
  differs.
- Purge pages ids by endpoint and deletes each with the full key. This replaces the
  container-delete expectation in `CosmosDbClientPurgeTests`.
- The client creates `unresolvedevents` with the `MultiHash` paths and TTL on. This replaces
  `CosmosDbEndpointContainerTtlTests`.
- `CosmosBicepContainerSyncTests` parses hierarchical keys and checks `unresolvedevents` against
  its expected path list (§5.6).

**Conformance, for every provider, in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`.**
New cross-endpoint isolation tests write the same `eventId` and `sessionId` to endpoints `A` and
`AB`, which is a prefix sibling. Then:

- each read member returns only its own endpoint's row;
- writes, patches, removes and archives on `A` leave `AB` unchanged;
- `PurgeMessages(A)` and `PurgeMessages(A, session)` leave `AB` intact;
- counts, paging, blocked and invalid lists stay per endpoint;
- the failed search returns only the requested endpoints.

No existing test covers this.

**Migration, against the emulator, in the Cosmos test project.** Seed two legacy containers with
unresolved rows, terminal rows carrying a TTL, soft-deleted rows, rows with `ttl: -1` and rows
without `ttl`. Migrate, then assert:

- the counts;
- the stamped `endpointId`;
- the remaining TTLs;
- an idempotent rerun;
- refusal while a writer is active;
- refusal when the target partition key is wrong.

**CLI (`tests/NimBus.CommandLine.Tests`):** `EndpointContainerProvisionerTests` goes;
`nb topology apply` no longer calls `az cosmosdb sql container create`; `nb container migrate`
parses its options.

**WebApp (`tests/NimBus.WebApp.Tests/AdminCosmosContainerTests.cs`):** `unresolvedevents` is
protected, and legacy endpoint containers stay protected until the marker lists them.

**CI.** The Cosmos suites keep running on `vnext-latest` under the "must not skip" gate. The
emulator ignores indexing policies, reports no RU charges and doesn't support .NET bulk, so the live
checks in §14 cover those gaps.

**Live, run by hand and recorded in the PR:** the RU comparison (§14 item 5), and a migration
rehearsal on a production-sized copy.

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | Prove the platform. Nothing ships | Every item in §14 answered, with results added to this folder |
| 1 | Store, Bicep, CLI, WebApp, migration command, tests, docs | Release build green; Cosmos and SQL conformance run without skips |
| 2 | Rehearse, then migrate each Cosmos deployment; delete legacy containers after the soak | Migration reports reconciled; no unmigrated legacy containers left |
| 3 | The following major: remove the obsolete `CosmosContainerDefaults` members and `--storage-provider` on `nb topology apply` | — |

## 10. Documentation

- `docs/adr/008-per-endpoint-cosmos-containers.md`: status "Superseded by ADR-017"; update
  `docs/adr/README.md`.
- `docs/storage-providers.md`: the Cosmos layout, purge semantics, and a replacement for the
  per-container TTL sections.
- `docs/azure-requirements.md`: the container list; drop "per-endpoint containers are created at
  runtime".
- `docs/cli.md`: `container migrate`, the note on `container copy`, and the `topology apply`
  deprecation.
- Other ADR-008 references: `docs/adr/002-centralized-resolver.md`,
  `docs/adr/010-pluggable-message-storage.md`, and the two CrmErpDemo adapter `docs/TDD.md` files.
- Release notes: an ⚠️ Breaking entry that links the runbook (§6.2).

## 11. Residual risks

1. **Noisy neighbour** (§7.2). Mitigated, not removed. The sharpest case is Admin → Delete all
   events on a large endpoint in production, which competes with the Resolver until it finishes
   (§5.5).
2. **Isolation bypass.** A future query that reaches the container without `EndpointScope` would
   bypass isolation. The structure and the recorded-query test make that visible in review.
3. **Emulator maturity.** Its hierarchical-key support is recent, so CI may fail spuriously or pass
   on behaviour the service doesn't share. Phase 0 and the live checks cover it.
4. **Migration with writers active.** The `_ts` quiet check can be overridden with `--force`.
5. **Hot-endpoint ceiling** of about 10,000 RU/s until splits (§7.2).
6. **Restore granularity** (§7.3).
7. **Divergence from DIS** (§7.4).

## 12. Alternatives considered

| Alternative | Verdict |
|---|---|
| Keep per-endpoint containers; put them on shared database throughput | Rejected. Microsoft advises against it for most workloads, caps it at 25 containers, and an existing database can't switch. It fixes none of the provisioning problems |
| Keep per-endpoint containers; move to a serverless account | Rejected. No provisioned-to-serverless conversion exists, so it means a new account and a migration anyway, and the provisioning problems remain |
| One container, partition key `/endpointId` | Rejected. Caps each endpoint at 20 GB and 10,000 RU/s |
| One container, partition key `/id` with the endpoint folded into the row id | Rejected. Changes ids that the API and UI expose, and every endpoint query fans out to all physical partitions |
| One container, three levels: `/endpointId`, `/sessionId`, `/id` | Rejected. `GetEventById` and `GetEventsByIds` receive only the composite row id, and an event id may contain `_`, so the session can't be recovered reliably. Sessions can be null. No routing gain while an endpoint fits in one physical partition |
| Purge with delete by partition key | Rejected. Preview, needs an account capability, and supports only full keys on hierarchical containers |
| Opt-in dual layout in a 4.x minor, default in the next major | Viable, but it runs two layouts and two conformance passes for a release cycle. §13 item 2 |
| Zero-downtime migration through the change feed | Rejected. Much more machinery to save a planned outage of minutes |
| Azure container copy jobs for the migration | Rejected. Copy jobs copy documents unchanged, and the rows need a new top-level `endpointId` |

## 13. Open decisions for the repo owner

1. **Release vehicle.** v4.0.0 is untagged and held for Spec 035 Phase 1. Landing this on master
   before the tag makes it part of v4.0.0; otherwise it is v5.0.0. Recommendation: don't hold v4.0.0
   for it.
2. **Migration style.** A one-shot migration in a major (recommended; simplest code) or an opt-in
   dual layout (§12).
3. **Default autoscale max** for `unresolvedevents`: 4,000 RU/s proposed, so a deployment with up to
   ten endpoints keeps at least its current total tracking capacity. Phase 0 confirms it.
4. **Outage length.** Copy everything during the outage (simplest), or only non-terminal rows and
   backfill the rest after the start (shorter outage).
5. **Storage containers page.** Retire the Admin → Storage containers page and the WebApp's Cosmos DB
   Operator role in a later major, once legacy containers are gone.
6. **Priority-based execution.** Enable it on the account, with WebApp reads, purge and migration at
   low priority. It is a best-effort feature with no SLA
   ([priority-based execution](https://learn.microsoft.com/azure/cosmos-db/priority-based-execution)).
7. **Failed search.** Collapse the cross-endpoint failed search into one
   `ARRAY_CONTAINS(@endpoints, c.endpointId)` query and retire the per-endpoint merge in
   `FailedEventPageCursor`.

## 14. Facts to verify before implementing (Phase 0)

1. **Throughput on existing endpoint containers.** Run `az cosmosdb sql container throughput show`
   against a real deployment to confirm the 400 RU/s cost baseline.
2. **Emulator support.** Check `vnext-latest` (CI's image) with .NET SDK 3.62.1 in gateway mode:
   - creating a `MultiHash` container;
   - read, create, upsert, `IfMatch` replace, patch and delete with the full key;
   - `ReadManyItemsAsync` with full keys;
   - SQL and LINQ queries with the endpoint predicate, including `GROUP BY`, `ORDER BY`,
     `OFFSET`/`LIMIT`, `TOP`, `ARRAY_CONTAINS`, `STARTSWITH(…, true)` and continuation tokens.

   Issue [#346](https://github.com/Azure/azure-cosmos-db-emulator-docker/issues/346) (hierarchical
   container creation failing, opened 2026-08-28) is closed, but it was reported against the Java
   SDK.
3. **Bicep.** `cosmosDB.bicep` deploys `unresolvedevents` with `MultiHash` at API version
   `2021-06-15`, or the resource moves to a newer API version.
4. **Prefix routing and scoping** on a live account:
   - the query metrics show that only the endpoint's partitions are read;
   - whether a prefix `QueryRequestOptions.PartitionKey` restricts results, which decides the
     optional second fence in §5.3.
5. **RU per operation**, legacy container against `unresolvedevents`, with and without the
   composite indexes:
   - operations: the guarded upsert path, a terminal upsert, a patch;
   - queries: `DownloadEndpointStateCount`, `DownloadEndpointStatePaging`, `GetEventsByFilter`, the
     failed search, the session counts.

   The results decide the indexing policy and the default throughput.
6. **Today's purge.** Whether `PurgeMessages(endpointId)` fails on a managed-identity deployment.
   This affects only the claim in §2.2.
7. **Quiet-check cost.** The RU cost of `SELECT VALUE MAX(c._ts)` on a large legacy container.

## 15. References

Microsoft documentation:

- [Hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys):
  up to three levels, prefix routing, the item id as the last level, set at creation only.
- [Service quotas](https://learn.microsoft.com/azure/cosmos-db/concepts-limits): 400 RU/s manual
  minimum, 500 databases plus containers per account, 20 GB and 10,000 RU/s per partition,
  autoscale billing floor.
- [Provision throughput](https://learn.microsoft.com/azure/cosmos-db/set-throughput): shared database
  throughput not recommended; 25-container limit.
- [Delete by partition key](https://learn.microsoft.com/azure/cosmos-db/how-to-delete-by-partition-key):
  preview; full keys only on hierarchical containers.
- [Linux vNext emulator](https://learn.microsoft.com/azure/cosmos-db/emulator-linux): feature table;
  no .NET bulk. Also the
  [GA announcement](https://devblogs.microsoft.com/cosmosdb/announcing-general-availability-of-the-azure-cosmos-db-vnext-emulator/).
- [Priority-based execution](https://learn.microsoft.com/azure/cosmos-db/priority-based-execution).
- [Change capacity mode](https://learn.microsoft.com/azure/cosmos-db/how-to-change-capacity-mode):
  serverless to provisioned only.
- [Data-plane role-based access](https://learn.microsoft.com/azure/cosmos-db/nosql/security/how-to-grant-data-plane-role-based-access):
  account, database and container scopes.
- [Understanding your bill](https://learn.microsoft.com/azure/cosmos-db/understand-your-bill).
- [Container ARM schema, 2021-06-15](https://learn.microsoft.com/azure/templates/microsoft.documentdb/2021-06-15/databaseaccounts/sqldatabases/containers).

Repo:

- ADR-008, ADR-010, Spec 026 (roles), Spec 030 (stale-write guard), Spec 031 (capacity controls),
  Spec 032 (stale-pending repair), Spec 034 (private networking).
- `src/NimBus.MessageStore.CosmosDb/` (`CosmosDbClient.cs`, `CosmosContainerDefaults.cs`,
  `CosmosDbMessageTrackingStore.*.cs`).
- `src/NimBus.CommandLine/EndpointContainerProvisioner.cs`.
- `deploy/bicep/templates/cosmosDB.bicep`.

## 16. Review trail

- 2026-09-26: drafted from an investigation of the per-endpoint layout. Not yet reviewed.
