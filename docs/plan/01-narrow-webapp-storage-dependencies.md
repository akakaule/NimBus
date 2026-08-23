# Plan 1: Narrow WebApp storage dependencies

## Goal

Replace unnecessary `INimBusMessageStore` dependencies in the WebApp with the smallest provider-neutral storage contracts each consumer actually uses. This completes the follow-up recorded in ADR-010, reduces test-double surface area, and makes provider boundaries visible without changing runtime behavior.

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
- `Services/AdminService.cs` and `Services/AdminService.Copy.cs`
- `Services/SeedDataService.cs`

Provider registration tests and affected WebApp tests are in scope. Changing storage behavior, schemas, API routes, JSON contracts, or provider selection is out of scope.

## Design constraints

- Follow the guidance in `INimBusMessageStore`: a consumer that uses one concern takes one narrow interface.
- A consumer using two or three concerns takes those interfaces explicitly. Keep the aggregate only when the class genuinely spans most storage concerns.
- Do not create replacement “mini aggregate” interfaces just to shorten constructors.
- Keep all narrow interfaces resolved to the same provider singleton. Mixed providers per contract remain out of scope under ADR-010.
- Rename misleading fields such as `_cosmosClient` to `_store`, `_metricsStore`, or another provider-neutral name in files already being changed.
- Preserve public HTTP behavior, authorization ordering, caching, exception translation, and cancellation behavior.

## Phase 1: inventory and architecture guard

1. Build a method-to-contract inventory for every target. Record it temporarily in the implementation pull request or directly in test data; do not infer the contract from the class name.
2. Add `StorageDependencyArchitectureTests.cs` under `tests/NimBus.WebApp.Tests`.
3. Use constructor reflection to assert that the first migration batch does not depend on `INimBusMessageStore`:
   - `MetricsImplementation` uses `IMetricsStore`.
   - `MessageImplementation`, `AuditImplementation`, `AuditLogService`, and `HandoffSettlementService` use `IMessageTrackingStore`.
4. Run the new tests and confirm they fail against the current constructors.

The guard should name explicit consumers. Do not forbid `INimBusMessageStore` across the entire assembly until the inventory proves that no legitimate aggregate consumer remains.

## Phase 2: migrate single-contract consumers

1. Change the five constructors identified above to their narrow interfaces.
2. Update fields and provider-neutral naming.
3. Replace broad fake stores in their unit tests with small handwritten fakes for the relevant interface.
4. Verify that controller responses, authorization failures, caching keys, redaction, and audit writes are byte-for-byte or assertion-for-assertion unchanged.
5. Extend the architecture guard to cover any other single-contract consumers found by the inventory.

## Phase 3: migrate multi-contract consumers

Migrate one class per commit so constructor and test failures remain attributable.

1. For each of `EventImplementation`, `EndpointImplementation`, `AgentImplementation`, `StorageHookImplementation`, `HeartbeatService`, `AdminService`, and `SeedDataService`, list the invoked storage members and map them to existing contracts.
2. Add a failing constructor-shape assertion for the selected class.
3. Inject the required narrow interfaces and update its tests.
4. If a class needs more than three storage contracts, first check whether it should be decomposed into an application service. Do not add a large constructor mechanically.
5. Leave `INimBusMessageStore` only where the inventory shows that the class is intentionally a cross-concern composition root.

`HeartbeatService` must continue to accept `IHeartbeatHistoryStore` as an optional capability, preserving third-party-provider compatibility from Spec 028.

## Phase 4: registration and documentation cleanup

1. Extend `CosmosDbMessageStoreRegistrationTests` and the corresponding SQL registration tests to prove that every narrow contract resolves and that all contracts point at the same underlying provider instance where required.
2. Update stale comments in `Startup.cs` that describe lifetimes only in terms of `INimBusMessageStore`.
3. Update ADR-010's negative consequence to state which aggregate consumers intentionally remain, or mark the follow-up complete if none remain.
4. Search production code for provider-specific field names attached to provider-neutral contracts and clean only the files touched by this plan.

## Verification

Run after each consumer batch:

```powershell
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.CosmosDb.Tests/NimBus.MessageStore.CosmosDb.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.SqlServer.Tests/NimBus.MessageStore.SqlServer.Tests.csproj -c Release
```

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

- Every WebApp aggregate dependency is justified by the inventory or replaced with narrow contracts.
- Narrow contracts resolve to the selected provider with unchanged lifetimes.
- Tests no longer implement dozens of unused aggregate methods for migrated consumers.
- ADR-010 accurately describes the remaining state.
- Release build and full solution tests pass.
