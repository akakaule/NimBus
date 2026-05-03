# Pluggable message storage providers (SQL Server + true Cosmos-free deployment)

> Spec: [`docs/specs/001-pluggable-message-storage/spec.md`](../docs/specs/001-pluggable-message-storage/spec.md)

## Summary

Today NimBus is hard-wired to Cosmos DB for all operational state (messages, audits, resolver state, subscriptions, endpoint metadata, metrics). This issue tracks the work to:

1. Decompose `ICosmosDbClient` into four provider-neutral contracts.
2. Ship a SQL Server provider as a separate NuGet package (`NimBus.MessageStore.SqlServer`).
3. Rename the Cosmos provider to `NimBus.MessageStore.CosmosDb`. Consumers update their package reference and registration call once; type namespaces are unchanged.
4. Make the deployment pipeline (`nb infra apply`, Bicep) support a **true SQL-only** deployment — no Cosmos resources provisioned, no Cosmos secrets required.
5. Validate provider registration in the builder so startup fails fast on zero / multiple providers.

A SQL-only customer must be able to install, provision, and operate NimBus without any reference to Cosmos.

## Provider-neutral contracts (FR-001..FR-005)

`ICosmosDbClient` is split into four separately-injectable abstractions, living in a dedicated abstractions assembly (recommended: `NimBus.MessageStore.Abstractions`):

- `IMessageTrackingStore` — message records, audits, resolver state, status transitions, search queries
- `ISubscriptionStore` — endpoint subscriptions / notifications
- `IEndpointMetadataStore` — endpoint metadata, heartbeats, enable/disable
- `IMetricsStore` — endpoint metrics, latency, failed-message insights, time-series

Constraints:
- A provider package implements **all four** contracts. Mixing providers per contract is out of scope for v1.
- Contracts MUST NOT expose Cosmos types (`PartitionKey`, `QueryDefinition`, `FeedIterator`, `CosmosException`) or SQL types (`SqlConnection`, `IDbCommand`).
- `MessageEntity`, `UnresolvedEvent`, `MessageAuditEntity` and related DTOs move into the abstractions assembly so `NimBus.Manager`, `NimBus.WebApp`, `NimBus.Resolver` no longer depend on a concrete provider package.

## Registration & validation (FR-010..FR-013)

- Exactly one storage provider per running app instance.
- Builder validates at `Build()` time, before any `IHostedService` starts.
- Registration via existing `AddNimBus()` builder pattern, e.g.:
  - `builder.AddCosmosDbMessageStore(...)`
  - `builder.AddSqlServerMessageStore(...)`
- No implicit default. The current `AddMessageStore()` → Cosmos behavior is removed; existing call sites must opt in explicitly.

## SQL Server provider (FR-020..FR-029)

- New project / NuGet package: `NimBus.MessageStore.SqlServer`.
- Persists message records, audit records, resolver state, subscriptions, endpoint metadata, heartbeats, metrics aggregates.
- Supports the full `ResolutionStatus` enum: `Pending`, `Deferred`, `Failed`, `TooManyRequests`, `DeadLettered`, `Unsupported`, `Published`, `Completed`, `Skipped`.
- Preserves all message correlation/timing fields (`EventId`, `MessageId`, `OriginatingMessageId`, `ParentMessageId`, `CorrelationId`, `SessionId`, `EndpointId`, `EventTypeId`, `EnqueuedTimeUtc`, `QueueTimeMs`, `ProcessingTimeMs`, retry/dead-letter metadata).
- Supports all WebApp/CLI query patterns: ID lookup, status filtering, chronological listing, audit search, message search with continuation, endpoint state counts, session state counts, blocked/invalid event lists, metrics aggregation, latency metrics, failed-message insights, time-series bucketing.
- Idempotent under duplicate writes / repeated status updates.
- Preserves resolver ordering/consistency guarantees (row locks vs optimistic concurrency decided in design).
- Configurable connection string, schema name, table naming.
- Health checks for connectivity + schema availability (mirrors `CosmosDbHealthCheck`).

## Schema management (DbUp, FR-030..FR-034)

- DbUp scripts as embedded resources in the provider package, applied idempotently.
- Two provisioning modes: **apply at startup** (dev) and **verify only** (prod, locked-down envs).
- Forward-only scripts. No rollback.
- Verify-only fails fast with a clear list of missing/outdated artifacts.
- Script set runnable from the deployment pipeline against an arbitrary connection string, independent of runtime.

## Deployment — CLI & Bicep (FR-040..FR-046)

- `nb infra apply --storage-provider {cosmos|sqlserver}` (default `cosmos` for back-compat).
- For `sqlserver`: `--sql-mode {provision|external}`. `provision` deploys Azure SQL via Bicep; `external` accepts `--sql-connection-string` (or Key Vault reference).
- `deploy.core.bicep` conditionally provisions Cosmos OR Azure SQL based on flag.
- `deploy.webapp.bicep` treats `cosmosAccountEndpoint` as optional; when absent, wires the SQL connection setting instead.
- Clear error when `--storage-provider sqlserver` is used without a `--sql-mode` choice.
- Must run end-to-end SQL-only against a fresh resource group.

## WebApp / API surface neutrality (FR-050..FR-054)

- WebApp services/controllers MUST NOT reference `Microsoft.Azure.Cosmos` directly. Refactor sites: `AdminService.cs`, `EndpointImplementation.cs`, `EventImplementation.cs`, `SeedDataService.cs`, `Startup.cs`.
- Rename Cosmos-named public surface (e.g., `StoragehookReceiveCosmosAsync` → `StoragehookReceiveAsync`); old name kept as `[Obsolete]` alias for one major version.
- Reword Cosmos-specific UI copy (e.g., "Delete events from Cosmos DB filtered by resolution status") to neutral terms.
- `IManagerClient.Resubmit` / `Skip` accept the relocated provider-neutral DTO.
- `NimBus.CommandLine` MUST NOT reference `Microsoft.Azure.Cosmos`.

## Backwards compatibility (FR-060..FR-063)

- Existing Cosmos deployments work after upgrade with **no** code, config, or data migration.
- Cross-provider data migration is out of scope.
- `AddNimBusPublisher`, `AddNimBusSubscriber`, `IEventHandler<T>` unchanged.
- Migration is a one-time package-reference + registration-call rename; type namespaces (`NimBus.MessageStore.*`) are unchanged.

## Testing & docs (FR-070..FR-072)

- Shared MSTest provider conformance suite — both Cosmos and SQL Server run the same suite. Existing Cosmos-coupled tests refactored into shared (behavior) and provider-specific (transport/setup) layers.
- CI runs the SQL conformance suite against `mcr.microsoft.com/mssql/server`. Note: Aspire pre-creates DBs empty, so provisioning must use a creator that issues DDL (not `EnsureCreated`).
- Document install / configure / provision / test / operate for SQL Server, including the SQL-only deployment.
- Document how contributors implement and distribute additional providers.

## Acceptance criteria

- [ ] **SC-001** — `nb infra apply --storage-provider sqlserver` + `AddSqlServerMessageStore(...)` produces a working deployment with **no** Cosmos resources, packages, or secrets.
- [ ] **SC-002** — Existing Cosmos solutions migrate by updating one package reference (`NimBus.MessageStore` → `NimBus.MessageStore.CosmosDb`) and one registration call (`AddMessageStore` → `AddCosmosDbMessageStore`); no schema, config, or data migration required.
- [ ] **SC-003** — SQL Server provider passes 100% of the shared conformance suite.
- [ ] **SC-004** — WebApp message list, detail, audit search, endpoint state, and metrics views return equivalent results across both providers for the same scenarios.
- [ ] **SC-005** — Full `ResolutionStatus` enum round-trips correctly in both providers.
- [ ] **SC-006** — `NimBus.MessageStore.SqlServer` and `NimBus.MessageStore.CosmosDb` can be packed and referenced independently.
- [ ] **SC-007** — Local SQL Server config achievable from docs in under 15 minutes.
- [ ] **SC-008** — Startup fails with a clear error when zero or multiple providers are registered.
- [ ] **SC-009** — `NimBus.WebApp`, `NimBus.CommandLine`, `NimBus.Resolver`, `NimBus.Manager` have zero compile-time references to `Microsoft.Azure.Cosmos` or SQL-specific types.

## Edge cases

- Existing Cosmos data must keep working after upgrade — no migration.
- Startup fails fast on zero or multiple providers.
- SQL transient failures surface clearly without corrupting message state.
- SQL schema may not exist on first start — behavior is explicit (apply vs verify-only).
- Locked-down envs where runtime cannot create/alter schema — supported via verify-only + pipeline-applied DbUp.
- `MessageContent` payloads persist without truncation up to documented limits.
- Provider-specific query limits must not break WebApp filtering / sorting / pagination.
- Provider-specific concepts (partition keys, RUs, continuation tokens, schemas, clustered indexes, migration tables) MUST NOT leak through neutral contracts.
- Bicep parameter changes (`cosmosAccountEndpoint` becoming optional, new SQL params) remain compatible with existing pipeline scripts.

## Out of scope

- Replacing Azure Service Bus as the transport.
- A universal ORM abstraction.
- Cross-provider data migration tooling.
- Mixing providers per contract.
- PostgreSQL / MySQL / Azure Table Storage providers in v1.
- Reworking `NimBus.Outbox.SqlServer` beyond registration-pattern alignment.
- Schema rollback.
- WebApp IA changes beyond what neutral naming requires.

## Open questions

- **SQL physical mapping per endpoint.** ADR-008 uses one Cosmos container per endpoint. SQL options: (a) one table per endpoint with dynamic DDL, (b) single `Events` table with `EndpointId` discriminator + composite indexes, (c) one schema per endpoint. Decided in design.
- **Abstractions location** — expanded `NimBus.Abstractions`, a new `NimBus.MessageStore.Abstractions`, or split.
- **Subscription store coupling** — whether `ISubscriptionStore` covers Notifications extension pathways or stays a strict record store.
- **Metrics retention** — explicit retention/cleanup policy for time-series, or operator-managed.

## Suggested labels

`enhancement` · `storage` · `sql-server` · `cosmos-db` · `breaking-change` · `infra` · `webapp` · `cli`
