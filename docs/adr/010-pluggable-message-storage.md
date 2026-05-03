# ADR-010: Pluggable Message Storage Providers

## Status
Accepted

## Context
Until v0.x NimBus hardcoded Cosmos DB at every layer: runtime registration, the
WebApp/CLI service code, the Bicep templates, the `nb infra apply` CLI, and even
some user-visible API names (`StoragehookReceiveCosmosAsync`). Customers whose
approved infrastructure does not include Cosmos DB could not adopt NimBus.

The spec at `docs/specs/001-pluggable-message-storage/spec.md` calls for:

1. A true SQL-only deployment (CLI + Bicep), not just runtime persistence swap.
2. SQL Server as the first alternate provider, full replacement of the entire
   `ICosmosDbClient` surface.
3. Symmetric package layout: `NimBus.MessageStore.CosmosDb` and
   `NimBus.MessageStore.SqlServer`, both implementing provider-neutral contracts.

## Decision

### Contract decomposition
The `ICosmosDbClient` 60+ method surface is split into four provider-neutral
contracts in `NimBus.MessageStore.Abstractions`:

- `IMessageTrackingStore` — message records, audit, resolver state, status transitions, search
- `ISubscriptionStore` — endpoint subscriptions (notifications)
- `IEndpointMetadataStore` — endpoint metadata + heartbeats
- `IMetricsStore` — endpoint metrics, latency, time-series

A convenience `INimBusMessageStore` aggregate combines all four for consumers
(like the WebApp) that touch every concern. Each provider package implements all
four; mixing providers per contract is out of scope for v1.

### Provider validation at builder time
`NimBusBuilder.Build()` (`src/NimBus.Core/Extensions/NimBusBuilder.cs`)
enumerates `IStorageProviderRegistration` registrations and throws with a clear
error message when zero or more than one provider is registered. This fires
before any `IHostedService` starts.

### Cosmos provider package
- Project: `NimBus.MessageStore.CosmosDb` (renamed from `NimBus.MessageStore`)
- Legacy `NimBus.MessageStore` package preserved as a `[TypeForwardedTo]` shim,
  marked `[Obsolete]`, scheduled for removal in a future major version.
- Centralized config-key parsing (`CosmosAccountEndpoint` / `cosmos` connection
  string / `CosmosConnection`) into `AddCosmosDbMessageStore()`.

### SQL Server provider package
- Project: `NimBus.MessageStore.SqlServer`
- Schema management: **DbUp** with embedded `.sql` resources; `AutoApply` and
  `VerifyOnly` provisioning modes.
- Physical mapping: **single table per concern with `EndpointId` discriminator**
  + composite indexes. No dynamic DDL (per-endpoint container model from ADR-008
  does not translate well to SQL).
- Idempotent status writes via `MERGE` on natural keys.
- Optimistic concurrency via `ROWVERSION` on `UnresolvedEvents`.
- `IHostedService`-driven schema initializer runs on startup based on
  `ProvisioningMode`.

### Realtime UI updates
The Cosmos webhook (`StoragehookReceiveCosmosAsync` → SignalR) was the only
mechanism for live UI updates. Replaced with `IMessageStateChangeNotifier`
fired from the Resolver write-path. The Cosmos webhook remains as a Cosmos-only
adapter for backwards compatibility.

### Cross-account copy operator tool
Cosmos-specific multi-account container copy is gated behind
`IStorageProviderCapabilities.SupportsCrossAccountCopy`. SQL deployments get a
clear "use SQL backup/restore" error rather than a broken UI.

### CLI + Bicep
- `nb infra apply` learns `--storage-provider {cosmos|sqlserver}`,
  `--sql-mode {provision|external}`, `--sql-connection-string`,
  `--sql-admin-login`, `--sql-admin-password`.
- `deploy.core.bicep` gates the Cosmos module on `storageProvider == 'cosmos'`
  and conditionally provisions Azure SQL via new
  `templates/azureSql.bicep` when `storageProvider == 'sqlserver' && sqlMode == 'provision'`.
- `deploy.webapp.bicep` makes `cosmosAccountEndpoint` optional, adds
  `sqlConnectionString`, validates exactly one is provided.

### Conformance test suite
`NimBus.Testing.Conformance.MessageTrackingStoreConformanceTests` is an
abstract MSTest base class. Each provider's test project subclasses it and
supplies a fresh store via `CreateStore()`. The suite covers all 9
`ResolutionStatus` values, idempotency, status transitions, audit history,
state counts, and lifecycle.

CI runs the in-memory + Cosmos-mock suites unconditionally and the SQL Server
suite against a `mcr.microsoft.com/mssql/server:2022-latest` service container.

## Consequences

### Positive
- A NimBus solution can be deployed with zero Cosmos resources (SC-001).
- Existing Cosmos-backed deployments continue to work without data migration.
- Future providers (PostgreSQL, MySQL, Azure Table Storage) implement the same
  four contracts and pass the same conformance suite — no further architecture
  work required.
- Operator tooling that only Cosmos can support is gated explicitly rather than
  silently breaking on SQL.

### Negative
- Two paths to maintain. CI now spins up a SQL Server container.
- Type-forwarder shim is permanent until a major version bump.
- Some WebApp services still take an `INimBusMessageStore` aggregate — splitting
  by contract is a follow-up cleanup.

## See also
- ADR-008: Per-endpoint Cosmos containers — SQL provider does not replicate this
  model; uses single-table-with-discriminator instead. See
  `docs/storage-providers.md` for the SQL schema layout.
- Spec: `docs/specs/001-pluggable-message-storage/spec.md`
