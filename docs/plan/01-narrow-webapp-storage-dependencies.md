# Plan 1: Narrow WebApp storage dependencies

## Outcome

Implemented. Commit `49bc2a0` migrated all WebApp controllers and services to narrow storage contracts and added provider-registration and architecture tests. The final cleanup removed stale Cosmos-specific field names and lifetime comments, strengthened the architecture guard, and updated ADR-010. The published aggregate remains available for provider facades and external compatibility.

## Goal

Replace unnecessary `INimBusMessageStore` dependencies in the WebApp with the smallest provider-neutral storage contracts each consumer actually uses. This completes the follow-up recorded in ADR-010 and makes provider boundaries visible without changing runtime behavior.

## Scope

Primary production targets are the WebApp controllers and services that currently inject `INimBusMessageStore`, including:

- `Controllers/ApiContract/MetricsImplementation.cs`
- `Controllers/ApiContract/MessageImplementation.cs`
- `Controllers/ApiContract/AuditImplementation.cs`
- `Controllers/ApiContract/EventImplementation.cs`
- `Controllers/ApiContract/EndpointImplementation.cs`
- `Controllers/ApiContract/AgentImplementation.cs`
- `Controllers/ApiContract/StorageHookImplementation.cs`
- `Services/AuditLogService.cs`
- `Services/HandoffSettlementService.cs`
- `Services/Heartbeat/HeartbeatService.cs`
- `Services/AdminService.cs`
- `Services/SeedDataService.cs`

Provider registration tests and affected WebApp tests are in scope. Changing storage behavior, schemas, API routes, JSON contracts, or provider selection is out of scope.

## Design constraints

- Follow the guidance in `INimBusMessageStore`: a consumer that uses one concern takes one narrow interface.
- A consumer using two or three concerns takes those interfaces explicitly.
- Do not create replacement “mini aggregate” interfaces just to shorten constructors.
- Keep all narrow interfaces resolved to the same provider singleton. Mixed providers per contract remain out of scope under ADR-010.
- Rename misleading fields such as `_cosmosClient` to `_store`, `_metricsStore`, or another provider-neutral name in files already being changed.
- Preserve public HTTP behavior, authorization ordering, caching, exception translation, and cancellation behavior.

## Verified inventory

The source inventory is complete:

| Consumer | Required contracts |
|---|---|
| `MetricsImplementation` | `IMetricsStore` |
| `MessageImplementation` | `IMessageTrackingStore` |
| `AuditImplementation` | `IMessageTrackingStore` |
| `AuditLogService` | `IMessageTrackingStore` |
| `HandoffSettlementService` | `IMessageTrackingStore` |
| `EventImplementation` | `IMessageTrackingStore` |
| `AgentImplementation` | `IMessageTrackingStore` |
| `StorageHookImplementation` | `IMessageTrackingStore` |
| `HeartbeatService` | `IEndpointMetadataStore`, `IServiceHealthStore`, and optional `IHeartbeatHistoryStore` |
| `EndpointImplementation` | `IMessageTrackingStore`, `ISubscriptionStore`, and `IEndpointMetadataStore` |
| `SeedDataService` | `IMessageTrackingStore`, `ISubscriptionStore`, and `IEndpointMetadataStore` |
| `AdminService` | `IMessageTrackingStore`; purge and resubmit partials use message-tracking operations |

No WebApp class needs the aggregate after this migration.

## Phase 1: add the architecture guard

1. Add `StorageDependencyArchitectureTests.cs` under `tests/NimBus.WebApp.Tests`.
2. Use constructor reflection to assert assembly-wide that no WebApp controller or service constructor depends on `INimBusMessageStore`.
3. Assert the expected narrow contracts for the migration targets so a future broadening fails visibly.
4. Run the new tests and confirm they fail against the current constructors.

## Phase 2: migrate single-contract consumers

1. Change these eight single-contract consumers:
   - `MetricsImplementation` to `IMetricsStore`;
   - `MessageImplementation`, `AuditImplementation`, `AuditLogService`, `HandoffSettlementService`, `EventImplementation`, `AgentImplementation`, and `StorageHookImplementation` to `IMessageTrackingStore`.
2. Update fields and provider-neutral naming.
3. Keep `NimBus.Testing.Conformance.InMemoryMessageStore` in existing WebApp tests. It is a shared, conformance-tested double and already satisfies the narrow contracts.
4. Verify that controller responses, authorization failures, caching keys, redaction, and audit writes are byte-for-byte or assertion-for-assertion unchanged.

## Phase 3: migrate multi-contract consumers

Migrate one class per commit so constructor and test failures remain attributable.

1. Migrate `HeartbeatService`, `EndpointImplementation`, and `SeedDataService` to the exact contract sets in the verified inventory.
2. Add a failing constructor-shape assertion for each selected class.
3. Inject the required narrow interfaces and update only tests whose construction changes.
4. Verify `AdminService`'s partials before changing its dependency. Narrow the live purge/resubmit operations to `IMessageTrackingStore` and update direct constructor calls explicitly because this is a source-shape change.
5. Complete the assembly-wide guard: no WebApp class may inject `INimBusMessageStore`.

`HeartbeatService` must continue to accept `IHeartbeatHistoryStore` as an optional capability, preserving third-party-provider compatibility from Spec 028.

## Phase 4: registration and documentation cleanup

1. Extend `CosmosDbMessageStoreRegistrationTests` and create `SqlServerMessageStoreRegistrationTests` to prove that every narrow contract resolves and that all contracts point at the same underlying provider instance where required.
2. Update stale comments in `Startup.cs` that describe lifetimes only in terms of `INimBusMessageStore`.
3. Update ADR-010 to mark the WebApp follow-up complete and explicitly retain `INimBusMessageStore` as a published convenience aggregate for providers and third-party consumers. Do not delete it merely because first-party production consumers no longer inject it.
4. Search production code for provider-specific field names attached to provider-neutral contracts and clean only the files touched by this plan.

## Verification

Run after each consumer batch:

```powershell
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.CosmosDb.Tests/NimBus.MessageStore.CosmosDb.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.SqlServer.Tests/NimBus.MessageStore.SqlServer.Tests.csproj -c Release
```

The live SQL and Cosmos conformance suites skip when their backends are unavailable. Do not treat skipped provider tests as proof. Supply `NIMBUS_SQL_TEST_CONNECTION` for a SQL Server 2022 instance and either `NIMBUS_COSMOS_TEST_CONNECTION` or `NIMBUS_COSMOS_TEST_ENDPOINT` plus `NIMBUS_COSMOS_TEST_KEY` for a Cosmos instance/emulator; set `NIMBUS_COSMOS_TEST_REQUIRED=1` when the Cosmos run must fail rather than skip.

Final gate:

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
```

Review the diff to confirm there are no route, OpenAPI, schema, or generated-client changes.

## Proposed pull requests

1. `refactor(webapp): narrow single-concern message-store dependencies`
2. `refactor(webapp): narrow multi-concern message-store dependencies`
3. `test(storage): guard provider-neutral contract registrations`

## Exit criteria

- No WebApp controller or service injects `INimBusMessageStore`.
- Each migrated consumer's declared storage surface matches its used surface.
- Narrow contracts resolve to the selected provider with unchanged lifetimes.
- Existing tests continue to reuse the conformance-tested in-memory store where appropriate.
- ADR-010 records completion of the WebApp cleanup and the deliberate retention of the published aggregate.
- Release build and full solution tests pass.
