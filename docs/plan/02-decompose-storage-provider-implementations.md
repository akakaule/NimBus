# Plan 2: Decompose storage-provider implementations

## Goal

Continue the established metrics/ACL/schema extraction pattern until `CosmosDbClient` and `SqlServerMessageStore` are compatibility facades over cohesive internal stores. Reduce file size and change collision without changing public types, storage schemas, provider behavior, or dependency-injection semantics.

## Scope

The plan covers both first-party providers and their tests:

- `NimBus.MessageStore.CosmosDb/CosmosDbClient.cs`
- `NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs`
- provider builder extensions and shared contexts
- Cosmos adapter tests, SQL integration tests, and shared conformance suites

No new storage abstraction, generic repository, cross-provider query language, schema migration, or provider mixing is in scope.

## Target structure

Keep these public facades and constructor shapes:

- `CosmosDbClient : INimBusMessageStore, IHeartbeatHistoryStore`
- `SqlServerMessageStore : INimBusMessageStore, IHeartbeatHistoryStore`

Move behavior behind internal concern-specific implementations:

```text
Provider facade
├── MessageTrackingStore
├── SubscriptionStore
├── EndpointMetadataStore
├── HeartbeatHistoryStore
├── ServiceHealthStore
├── MetricsStore              (already extracted)
├── EventSchemaStore          (already extracted)
└── AccessControlStore        (already extracted)
```

The facade delegates contract methods and remains the single DI instance exposed as `INimBusMessageStore`. This preserves source compatibility and the “one selected provider” decision in ADR-010.

## Shared-provider constraints

- Cosmos internal stores receive container access delegates backed by the existing single-flight container cache. Do not create independent `CosmosClient` instances or duplicate container-creation logic.
- Failed cached Cosmos container creation must retain the existing eviction/retry behavior.
- SQL internal stores receive `SqlServerStoreContext`, preserving connection opening, bracket quoting, timeout, and exception translation.
- Preserve Newtonsoft.Json serialization, SQL text, partition keys, TTL values, request options, continuation tokens, and provider exception types exactly.
- Do not move SQL statements and change them in the same commit.
- Do not add cancellation-token overloads that make existing public calls with `default` ambiguous.

## Phase 1: pin facade and registration behavior

1. Add or extend registration tests for both providers to prove:
   - all narrow interfaces resolve;
   - `INimBusMessageStore` retains its current singleton lifetime;
   - `IHeartbeatHistoryStore` resolves to the same facade;
   - existing public constructors remain available.
2. Add delegation-focused unit tests using recording Cosmos adapters and SQL context fakes where practical.
3. Run the new tests and verify that a deliberately missing delegate fails before extraction begins.

## Phase 2: extract small independent concerns

Extract one concern in both providers before moving to the next:

1. `SubscriptionStore`
2. `ServiceHealthStore`
3. `HeartbeatHistoryStore`

For each concern:

1. Add the internal provider class and move the existing methods without semantic edits.
2. Have the facade delegate each interface member.
3. Run the matching shared conformance suite against Cosmos, SQL Server, and in-memory baselines.
4. Compare SQL text, Cosmos query definitions, partition keys, request options, and exception handling with the pre-move diff.
5. Commit the concern before beginning the next one.

## Phase 3: extract endpoint metadata and heartbeat state

Endpoint metadata and current heartbeat state share physical storage in Cosmos, so they should remain one cohesive `CosmosDbEndpointMetadataStore`. Mirror the same contract boundary in SQL even though SQL uses separate tables.

1. Move `IEndpointMetadataStore` behavior and heartbeat schedule/claim logic.
2. Preserve the rule that service-health records never appear as endpoint metadata.
3. Preserve heartbeat rollup limits, claim atomicity, timeout sweeping, and `PrunesHeartbeatHistoryAutomatically` behavior.
4. Run `EndpointMetadataStoreConformanceTests`, heartbeat-history conformance, retention tests, and provider-specific race/claim tests.

## Phase 4: extract message tracking last

Message tracking owns the largest and most coupled surface: state transitions, searches, message history, audit history, event reports, purge, handoff lookups, and status counts.

1. Group existing methods by behavior inside the original facade and add characterization tests before moving them.
2. Move the complete `IMessageTrackingStore` implementation to `CosmosDbMessageTrackingStore` and `SqlServerMessageTrackingStore`; avoid fragmenting a single contract across several loosely coordinated classes unless tests reveal a stable internal seam.
3. Preserve exact-match authorization support in audit queries, search pagination semantics, status lifecycle, event-report enrichment, retention, and archive behavior.
4. Keep provider-neutral helpers such as `EndpointErrorListFormat` in the abstractions project.
5. Run all message-store conformance and provider-specific tests before removing the old implementations from the facades.

## Phase 5: finish the facades

1. Reduce each public provider class to construction, shared provider plumbing, and straightforward delegation.
2. Add XML documentation explaining that it is a compatibility aggregate facade.
3. Ensure internal stores are not independently public or independently registered.
4. Update `docs/architecture.md` and `docs/storage-providers.md` so they no longer describe provider logic as concentrated in one client. In `docs/architecture.md`, specifically replace the line 759 tradeoff about logic concentrated in `CosmosDbClient` and correct the stale line 770 path `src/NimBus.MessageStore/CosmosDbClient.cs` to the current provider-package path.
5. Run a source-compatibility check against representative existing constructor calls in tests and samples.
6. Decide separately whether to seal `CosmosDbClient`. It currently has no virtual members or repository subclasses, but sealing a public type is an API change and must not be folded silently into this refactor. Default to leaving it unsealed unless a separately reviewed compatibility decision approves the change.

## Verification

Targeted gates after every concern:

```powershell
dotnet test tests/NimBus.MessageStore.CosmosDb.Tests/NimBus.MessageStore.CosmosDb.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.InMemory.Tests/NimBus.MessageStore.InMemory.Tests.csproj -c Release
dotnet test tests/NimBus.MessageStore.SqlServer.Tests/NimBus.MessageStore.SqlServer.Tests.csproj -c Release
```

The SQL test project requires its normal SQL Server test dependency. Do not treat skipped provider tests as proof.

Final gate:

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
```

Review the final diff for accidental DDL, query, serialization, public API, or package changes.

## Proposed pull requests

1. `test(storage): pin aggregate facade and provider registrations`
2. `refactor(storage): extract subscription and service-health stores`
3. `refactor(storage): extract heartbeat storage concerns`
4. `refactor(storage): extract endpoint metadata stores`
5. `refactor(storage): extract message-tracking stores`
6. `docs(storage): document provider aggregate facades`

## Exit criteria

- Both public provider facades preserve their constructors and interfaces.
- Every cohesive storage concern lives in a focused internal implementation.
- Shared conformance suites pass against all first-party providers.
- No schema, query, serialization, TTL, continuation-token, or exception semantic changes appear in the refactor.
- Architecture documentation matches the new structure.
