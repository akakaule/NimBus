# Feature Specification: Pluggable Message Storage Providers

Feature Branch: `001-pluggable-message-storage`  
Created: 2026-05-02  
Updated: 2026-05-02  
Status: Draft (revised after review)  
Input: User description: "Allow NimBus users to choose their own storage provider instead of requiring Cosmos DB for all message and state storage. SQL Server should be available as a separately downloadable extension/NuGet package and configurable through the NimBus builder."

## Deployment Mode (resolved)

NimBus v1 MUST support a **true SQL-only deployment**. Organizations whose approved infrastructure does not include Cosmos DB must be able to install, provision, and operate NimBus without referencing or paying for any Cosmos resources. This commitment shapes the rest of the spec — `nb infra apply`, the Bicep templates, the WebApp configuration, and the runtime registration must all support a no-Cosmos path.

## Provider Scope (resolved)

The SQL Server provider is a **full replacement** for the current `ICosmosDbClient` surface. That surface is decomposed into four provider-neutral contracts:

- **Message tracking** — message records, lifecycle status, audit records, resolver state, status transitions, search queries
- **Subscription store** — endpoint subscription/notification records
- **Endpoint metadata store** — endpoint metadata and heartbeat
- **Metrics store** — aggregated endpoint metrics, latency metrics, failed-message insights, time-series bucketing

A storage provider package implements all four contracts. Mixing providers per contract is out of scope for v1.

## User Scenarios & Testing

### User Story 1 - Configure a storage provider explicitly (Priority: P1)

As a NimBus solution owner, I want to configure the platform to use a storage provider of my choice, so that NimBus can fit into my organization's approved infrastructure, operational practices, and data governance requirements.

Why this priority: Core value of the feature.

Independent Test: Create a NimBus deployment that references the SQL Server provider package, configures it through the NimBus builder, sends messages through Azure Service Bus, and verifies message state, audits, subscriptions, endpoint metadata, and metrics are stored and queryable via the configured provider.

Acceptance Scenarios:

1. Given a NimBus application with the SQL Server storage provider installed and registered, When messages flow through the system, Then all message tracking, audit, subscription, endpoint metadata, and metrics records are persisted to SQL Server.
2. Given a NimBus application with the Cosmos DB storage provider installed and registered, When messages flow through the system, Then existing Cosmos behavior is preserved end-to-end.
3. Given a NimBus application with no storage provider registered, When the application starts, Then startup fails with a clear error explaining that exactly one storage provider must be registered.
4. Given a NimBus application with multiple storage providers registered, When the application starts, Then startup fails with a clear error explaining that only one storage provider may be active.

---

### User Story 2 - Install storage as an extension package (Priority: P1)

As a NimBus user, I want each storage provider to be delivered as a separate NuGet package, so that I only take the dependency I need.

Why this priority: Distribution model is separate package, not built-in.

Independent Test: Pack `NimBus.MessageStore.SqlServer` as its own NuGet package, install into a sample NimBus application without referencing the Cosmos package, configure via the builder, and run the message lifecycle suite against SQL Server. Repeat the exercise mirrored for the Cosmos package.

Acceptance Scenarios:

1. Given a NimBus application that does not reference `NimBus.MessageStore.SqlServer`, When it builds, Then it does not transitively reference SQL Server-specific libraries (Microsoft.Data.SqlClient, DbUp, etc.).
2. Given a NimBus application that does not reference `NimBus.MessageStore.CosmosDb`, When it builds, Then it does not transitively reference Cosmos-specific libraries (Microsoft.Azure.Cosmos).
3. Given a NimBus application that references either provider package, When the user calls the registration extension method, Then the storage services are registered in DI and validated by the builder.

---

### User Story 3 - Preserve management and resolver behavior (Priority: P1)

As an operator using the management UI and resolver, I want the same message statuses, history, and recovery workflows to work regardless of the backing storage provider.

Independent Test: Run existing message flow scenarios against both Cosmos and SQL providers, then compare externally visible status transitions, query results, and recovery operations.

Acceptance Scenarios:

1. Given any storage provider, When a message moves through its lifecycle, Then the recorded resolution status uses the same enum values across providers (`Pending`, `Deferred`, `Failed`, `TooManyRequests`, `DeadLettered`, `Unsupported`, `Published`, `Completed`, `Skipped`).
2. Given a message is successfully processed, Then the configured provider records `Completed`.
3. Given a message fails, is retried, or is dead-lettered, Then the configured provider records the same failure information available with Cosmos today (retry counts, dead-letter reason, error description, originating message id).
4. Given the management web app queries history, search, audits, endpoint state, or metrics, Then the UI returns equivalent results across providers for the same message flow scenarios.

---

### User Story 4 - Provider-specific setup without leaking into core (Priority: P2)

As a platform maintainer, I want provider-specific connection strings, schemas, health checks, and migrations to live in the provider package, so that NimBus core stays provider-neutral.

Independent Test: After implementation, verify that provider-specific types, connection-string names, migrations, and schema setup do not appear in `NimBus.Abstractions`, `NimBus.Core`, `NimBus.Resolver`, `NimBus.Manager`, `NimBus.WebApp`, or `NimBus.CommandLine` business logic.

Acceptance Scenarios:

1. Given the SQL Server package is installed, Then it exposes options for connection string, schema name, table naming, and provisioning behavior.
2. Given the SQL Server package is not installed, Then NimBus core builds without SQL Server dependencies.
3. Given SQL Server is unavailable, Then health checks report unhealthy with actionable details.

---

### User Story 5 - Enable future storage providers (Priority: P2)

As a contributor, I want to implement another storage provider using documented contracts, so that NimBus can support PostgreSQL, MySQL, Azure Table Storage, or other stores without redesigning storage again.

Independent Test: Create a minimal in-memory or fake provider that satisfies the four contracts and passes the shared conformance suite.

Acceptance Scenarios:

1. Given a contributor implements the storage provider contracts, When the provider is registered, Then NimBus uses it without changes to message publishing, handling, resolver, manager, or management workflows.
2. Given a provider passes the shared conformance suite, When used in a NimBus deployment, Then it supports the full lifecycle.
3. Given the documentation, a contributor can distribute the new provider as an independent package.

---

### User Story 6 - Deploy NimBus without Cosmos DB (Priority: P1)

As an operator deploying NimBus into an organization that does not approve Cosmos DB, I want `nb infra apply` and the Bicep templates to provision a SQL-only deployment, so that I am not paying for or managing a Cosmos account that the runtime never touches.

Why this priority: Without this story, SC-001 is provably false — the deployment pipeline still forces Cosmos regardless of runtime registration.

Independent Test: Run `nb infra apply --storage-provider sqlserver --solution-id ... --environment ...` against a fresh resource group; verify no Cosmos account is created, that an Azure SQL resource is provisioned (or referenced if external), that the web app is deployed without `cosmosAccountEndpoint`, and that messages flow end-to-end.

Acceptance Scenarios:

1. Given `nb infra apply --storage-provider sqlserver`, When deployment completes, Then no Cosmos account is provisioned in the target resource group.
2. Given `nb infra apply --storage-provider sqlserver --sql-mode external --sql-connection-string ...`, When deployment completes, Then NimBus is configured against the externally provisioned SQL Server.
3. Given `nb infra apply --storage-provider cosmos` (or no flag, preserving current behavior), When deployment completes, Then existing Cosmos provisioning behavior is unchanged.
4. Given the web app Bicep parameters, When `cosmosAccountEndpoint` is omitted, Then it is not written to app settings, and the equivalent SQL connection setting is wired in instead.

---

## Edge Cases

- Existing Cosmos-backed deployments must continue to work after upgrade with no data migration.
- Startup must fail fast with a clear error if zero storage providers are registered.
- Startup must fail fast with a clear error if more than one storage provider is registered.
- The NimBus builder MUST validate provider registration at `Build()` time, before any `IHostedService` starts.
- Message status updates must remain idempotent under Service Bus delivery, retries, and resolver re-processing.
- SQL Server transient failures must be surfaced clearly and must not corrupt message state.
- SQL Server schema may not exist on first startup; behavior must be explicit and configurable (auto-apply vs verify-only).
- SQL Server may run in locked-down environments where the runtime identity cannot create or alter schema.
- NimBus upgrades that require schema changes must support an apply-separately-from-runtime path (DbUp scripts can be run by the deployment pipeline against an arbitrary connection string).
- Large payloads (`MessageContent`) must persist without truncation up to documented limits.
- Provider-specific query limitations must not break the WebApp's filtering, sorting, and pagination behavior.
- Cosmos-specific concepts (partition keys, request units, continuation tokens) must not appear in provider-neutral contracts.
- SQL-specific concepts (schemas, clustered indexes, migration tables) must not appear in provider-neutral contracts.
- WebApp and CLI code paths that today reference Cosmos types directly (`QueryDefinition`, `PartitionKey`, `CosmosException`) must move behind provider-neutral interfaces before the SQL provider can satisfy SC-001.
- Public WebApp surface that bears Cosmos in its name (`StoragehookReceiveCosmosAsync`, UI copy referencing "Cosmos DB") must be renamed to neutral terms; old names are preserved as `[Obsolete]` aliases for one major version.
- Bicep parameter shape changes (`cosmosAccountEndpoint` becoming optional, new SQL parameters) must remain compatible with existing pipeline scripts that pass the old parameter set.

## Requirements

### Functional Requirements

#### Contracts

- FR-001: NimBus MUST define provider-neutral contracts for the full operational state surface currently owned by `ICosmosDbClient`, organized as separately injectable abstractions:
  - `IMessageTrackingStore` — message records, audits, resolver state, status transitions, search queries (replaces the bulk of today's `ICosmosDbClient`)
  - `ISubscriptionStore` — endpoint subscriptions/notifications
  - `IEndpointMetadataStore` — endpoint metadata, heartbeats, enable/disable operations
  - `IMetricsStore` — endpoint metrics, latency, failed-message insights, time-series
- FR-002: A provider package MUST implement all four contracts. Mixing providers per contract is out of scope for v1.
- FR-003: Provider-neutral contracts MUST live in a dedicated abstractions location (recommended: `NimBus.MessageStore.Abstractions` package, or expansion of `NimBus.Abstractions`). Final placement is decided in the design phase.
- FR-004: Provider-neutral contracts MUST NOT contain Cosmos-specific types (`PartitionKey`, `QueryDefinition`, `FeedIterator`, `CosmosException`) or SQL-specific types (`SqlConnection`, `IDbCommand`).
- FR-005: `MessageEntity`, `UnresolvedEvent`, `MessageAuditEntity`, and related DTOs returned by storage contracts MUST be relocated into the abstractions assembly so that consumers (`NimBus.Manager`, `NimBus.WebApp`, `NimBus.Resolver`) do not depend on a concrete provider package.

#### Provider registration & validation

- FR-010: NimBus MUST require exactly one storage provider to be registered per running application instance.
- FR-011: The NimBus builder MUST validate provider registration at `Build()` time (or equivalent terminal call) and fail before any `IHostedService` starts when zero or more-than-one providers are registered.
- FR-012: Storage provider registration MUST be exposed through the existing `AddNimBus()` builder pattern via provider-specific extension methods (e.g., `builder.AddCosmosDbMessageStore(...)`, `builder.AddSqlServerMessageStore(...)`).
- FR-013: There MUST NOT be an implicit default storage provider. The current behavior of `AddMessageStore()` registering Cosmos implicitly is removed; existing call sites must opt in to a specific provider.

#### SQL Server provider

- FR-020: The SQL Server provider MUST be implemented as `NimBus.MessageStore.SqlServer`, a separate project and NuGet package.
- FR-021: The Cosmos DB provider MUST be relocated/renamed to `NimBus.MessageStore.CosmosDb` for symmetry. Existing consumers update their package reference and registration call to the new name; type namespaces (`NimBus.MessageStore.*`) are unchanged.
- FR-022: The SQL Server provider MUST persist all data necessary to reproduce existing Cosmos-backed behavior: message records, audit records, resolver state, subscriptions, endpoint metadata, heartbeats, and metrics aggregates.
- FR-023: The SQL Server provider MUST support the full `ResolutionStatus` enum: `Pending`, `Deferred`, `Failed`, `TooManyRequests`, `DeadLettered`, `Unsupported`, `Published`, `Completed`, `Skipped`. Each value MUST round-trip through the provider conformance suite.
- FR-024: The SQL Server provider MUST preserve `EventId`, `MessageId`, `OriginatingMessageId`, `ParentMessageId`, `CorrelationId`, `SessionId`, `EndpointId`, `EventTypeId`, `EnqueuedTimeUtc`, retry/dead-letter metadata, and per-message timing fields (`QueueTimeMs`, `ProcessingTimeMs`).
- FR-025: The SQL Server provider MUST support the WebApp/CLI query patterns: lookup by IDs, status filtering, chronological listing, message detail retrieval, audit search, message search with continuation, endpoint state counts, session state counts, blocked/invalid event lists, metrics aggregation, latency metrics, failed-message insights, and time-series bucketing.
- FR-026: The SQL Server provider MUST handle duplicate writes and repeated status updates idempotently.
- FR-027: The SQL Server provider MUST preserve the ordering and consistency guarantees the resolver relies on for state updates. The implementation choice (row locks, optimistic concurrency token, etc.) is decided in the design phase.
- FR-028: The SQL Server provider MUST expose configuration options for connection string, schema name, and table naming.
- FR-029: The SQL Server provider MUST include health checks for connectivity and required schema availability, mirroring the existing `CosmosDbHealthCheck` pattern.

#### Schema management

- FR-030: The SQL Server provider MUST use **DbUp** for schema provisioning. Schema scripts MUST be embedded resources in the provider package and applied idempotently.
- FR-031: The SQL Server provider MUST support two provisioning modes: "apply at startup" (intended for development) and "verify only at startup" (intended for production-locked environments where schema is applied externally).
- FR-032: NimBus upgrades that include schema changes MUST add forward-only DbUp scripts. Schema rollback is out of scope.
- FR-033: The verify-only mode MUST fail fast with a clear error listing missing or out-of-date schema artifacts.
- FR-034: The DbUp script set MUST be runnable from the deployment pipeline against an arbitrary connection string, independent of the application runtime.

#### Deployment (CLI & Bicep)

- FR-040: `nb infra apply` MUST accept a `--storage-provider {cosmos|sqlserver}` option. When omitted, it defaults to `cosmos` for backwards compatibility.
- FR-041: When `--storage-provider sqlserver` is used, `nb infra apply` MUST accept a `--sql-mode {provision|external}` option. `provision` deploys an Azure SQL resource via Bicep; `external` accepts `--sql-connection-string` (or a Key Vault reference) for an externally managed SQL Server.
- FR-042: `deploy.core.bicep` MUST be parameterized so the Cosmos account/database is conditionally provisioned only when the storage provider is `cosmos`.
- FR-043: `deploy.core.bicep` MUST conditionally provision an Azure SQL resource (server + database) when the storage provider is `sqlserver` and SQL mode is `provision`.
- FR-044: `deploy.webapp.bicep` MUST treat `cosmosAccountEndpoint` as optional. When absent, it MUST NOT be written to app settings; the equivalent SQL connection setting MUST be wired in instead.
- FR-045: `nb infra apply` MUST emit a clear error when `--storage-provider sqlserver` is used without either `--sql-mode provision` or `--sql-mode external --sql-connection-string ...`.
- FR-046: `nb infra apply` MUST be runnable end-to-end with `--storage-provider sqlserver` against a fresh resource group, producing a working SQL-only NimBus deployment.

#### WebApp / API surface

- FR-050: WebApp services and controllers MUST NOT reference `Microsoft.Azure.Cosmos` types directly. Existing usages in `AdminService.cs`, `EndpointImplementation.cs`, `EventImplementation.cs`, `SeedDataService.cs`, and `Startup.cs` MUST be moved behind provider-neutral contracts.
- FR-051: Public API surface that names a specific storage technology (e.g., `StoragehookReceiveCosmosAsync`) MUST be renamed to a neutral form (e.g., `StoragehookReceiveAsync`). The legacy method/route MUST remain as an `[Obsolete]` alias for one major version, routing to the new implementation.
- FR-052: User-visible UI copy that names a specific storage technology (e.g., "Delete events from Cosmos DB filtered by resolution status") MUST be reworded to neutral terms.
- FR-053: `IManagerClient.Resubmit` and `IManagerClient.Skip` MUST accept the relocated provider-neutral message DTO (per FR-005), not a Cosmos-coupled type.
- FR-054: The `NimBus.CommandLine` project MUST NOT reference `Microsoft.Azure.Cosmos` directly; any container introspection used for diagnostic commands moves behind the storage contracts.

#### Backwards compatibility

- FR-060: Existing Cosmos DB deployments MUST NOT require data migration to keep working after upgrade.
- FR-061: Cross-provider data migration (Cosmos → SQL or vice versa) is out of scope for v1.
- FR-062: Existing public publishing and subscribing APIs (`AddNimBusPublisher`, `AddNimBusSubscriber`, `IEventHandler<T>`) MUST NOT change.
- FR-063: Renaming `NimBus.MessageStore` to `NimBus.MessageStore.CosmosDb` is a one-time breaking change for downstream consumers — they update their package reference and registration call. Type namespaces (`NimBus.MessageStore.*`) are unchanged so `using` directives keep working.

#### Testing & documentation

- FR-070: NimBus MUST include a **shared provider conformance test suite**. Both Cosmos and SQL Server implementations MUST run the same suite. Existing Cosmos-coupled tests MUST be refactored into shared (behavior) and provider-specific (transport/setup) layers.
- FR-071: NimBus MUST document how to install, configure, provision, test, and operate the SQL Server storage provider, including the SQL-only deployment path.
- FR-072: NimBus MUST document how contributors implement and distribute additional storage providers.

### Non-Functional Requirements

- NFR-001: Provider-neutral abstractions MUST NOT expose Cosmos- or SQL-specific concepts.
- NFR-002: SQL Server package install MUST NOT be required for users who continue using Cosmos DB.
- NFR-003: Cosmos package install MUST NOT be required for users who use SQL Server only.
- NFR-004: SQL Server provider operations MUST be safe under concurrent message processing.
- NFR-005: SQL Server provider errors MUST include actionable messages without exposing secrets.
- NFR-006: The SQL Server conformance suite MUST run in CI against a disposable SQL Server container (e.g., `mcr.microsoft.com/mssql/server`), with documented Aspire-friendly setup. Note the known-issue pattern: Aspire pre-creates databases empty, so provisioning logic must use a creator that issues DDL rather than relying on `EnsureCreated`-style no-ops.
- NFR-007: Documentation MUST include minimal working configurations for both Aspire/local and production-style deployments, for both Cosmos and SQL Server providers.
- NFR-008: The feature MUST be compatible with the repository's `net10.0` target and existing packaging conventions.
- NFR-009: Provider conformance tests MUST verify behavior, not provider implementation details.

## Key Entities

- Message Storage Provider — implementation set covering message tracking, subscriptions, endpoint metadata, and metrics contracts.
- Message Record (`MessageEntity`) — persisted representation of a message moving through NimBus.
- Resolution Status — operational state of a message; full enum: `Pending`, `Deferred`, `Failed`, `TooManyRequests`, `DeadLettered`, `Unsupported`, `Published`, `Completed`, `Skipped`.
- Message Audit Record — historical event/transition for diagnostics and management UI.
- Resolver State (`UnresolvedEvent`) — state maintained by the resolver per endpoint and message lifecycle position.
- Endpoint Subscription — notification subscription record per endpoint.
- Endpoint Metadata + Heartbeat — operational state for an endpoint.
- Endpoint Metrics — aggregated metrics, latency, failed-message insights, and time-series bucketed data.
- Storage Provider Options — provider-specific configuration (connection string, schema name, table naming, provisioning mode, health check thresholds).
- Provider Conformance Test Suite — shared MSTest suite that validates each provider against the same behavioral specification.

## Success Criteria

### Measurable Outcomes

- SC-001: A NimBus solution can be deployed entirely without Cosmos DB by running `nb infra apply --storage-provider sqlserver` and registering `AddSqlServerMessageStore(...)` in the host. No Cosmos resources are provisioned, no Cosmos packages are referenced, and no Cosmos secrets are required.
- SC-002: Existing Cosmos-backed solutions migrate by updating one package reference (`NimBus.MessageStore` → `NimBus.MessageStore.CosmosDb`) and one registration call (`AddMessageStore` → `AddCosmosDbMessageStore`); no schema, configuration, or data migration is required, and `using` directives keep working unchanged.
- SC-003: The SQL Server provider passes 100% of the shared conformance test suite.
- SC-004: WebApp message list, message detail, audit search, endpoint state, and metrics views return equivalent results across Cosmos and SQL Server providers for the same message flow scenarios.
- SC-005: Resolver status transitions across the full `ResolutionStatus` enum are persisted correctly in both providers.
- SC-006: `NimBus.MessageStore.SqlServer` and `NimBus.MessageStore.CosmosDb` can be packed and referenced independently.
- SC-007: A developer can configure SQL Server storage from documentation in under 15 minutes for local development.
- SC-008: Startup fails with a clear configuration error when zero or multiple storage providers are registered.
- SC-009: `NimBus.WebApp`, `NimBus.CommandLine`, `NimBus.Resolver`, and `NimBus.Manager` assemblies have zero compile-time references to `Microsoft.Azure.Cosmos` or SQL-specific types after the refactor.

## Assumptions

- Message transport remains Azure Service Bus; this feature only changes persistent state storage.
- Transactional outbox storage (`NimBus.Outbox.SqlServer`) is distinct from message tracking and is not modified by this feature beyond adopting consistent registration patterns where it improves symmetry.
- Provider selection is application-level, not per-endpoint or per-message-type.
- Message payload bodies (`MessageContent`) are stored in the message store today and will be in the SQL Server provider as well.
- SQL Server is the first alternate provider but the feature delivers a general provider model usable for future PostgreSQL/MySQL/etc. providers.
- DbUp is the chosen schema provisioning tool; EF Core migrations and hand-managed scripts have been considered and rejected for v1.
- The Cosmos provider package is renamed to `NimBus.MessageStore.CosmosDb`. Existing consumers update their package reference and registration call; type namespaces are unchanged.

## Out of Scope

- Replacing Azure Service Bus as the transport.
- A universal ORM abstraction across all possible databases.
- Cross-provider data migration tooling (Cosmos → SQL or vice versa).
- Multiple active storage providers in the same running component, including mixing providers per contract.
- Adding PostgreSQL, MySQL, Azure Table Storage, or other providers in v1.
- Reworking `NimBus.Outbox.SqlServer` beyond registration-pattern alignment.
- Schema rollback support for the SQL Server provider.
- WebApp information-architecture changes beyond what neutral naming requires.

## Open Questions

- **Per-endpoint physical mapping in SQL.** ADR-008 uses one Cosmos container per endpoint. SQL options to evaluate in design: (a) one table per endpoint with dynamic DDL, (b) single `Events` table with `EndpointId` discriminator and composite indexes, (c) one schema per endpoint. Decision deferred to design phase.
- **Abstractions location.** Whether contracts live in an expanded `NimBus.Abstractions`, a new `NimBus.MessageStore.Abstractions` package, or split between them.
- **Subscription store coupling.** Whether `ISubscriptionStore` should additionally cover the Notifications extension pathways or remain strictly a record store.
- **Metrics retention.** Whether SQL implementations should expose an explicit retention/cleanup policy for time-series data, or leave that to the operator.

## Resolved Questions (from prior revision)

- v1 supports a true SQL-only deployment, including CLI and Bicep changes (see User Story 6, FR-040–FR-046).
- SQL Server provider is a full replacement for `ICosmosDbClient` (messages, audits, state, subscriptions, endpoint metadata, heartbeats, metrics, time-series). See FR-001/FR-002.
- Schema management uses **DbUp**.
- Cosmos provider package is renamed to `NimBus.MessageStore.CosmosDb`. Consumers update their package reference and registration call once.
- SQL Server package is named `NimBus.MessageStore.SqlServer` (matches `NimBus.Outbox.SqlServer` precedent).
- Provider validation at startup is performed by the NimBus builder at `Build()` time.
- Message payload bodies are stored in the message store; the SQL provider preserves this.
- Conformance suite is shared (both providers run the same MSTest suite).
- There is no implicit default provider — applications must register one explicitly.
