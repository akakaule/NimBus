# Storage-Layer Refactor: Pagination Caps, Shared Rules, Concern Carve-Outs

## Context

The three storage providers (Cosmos DB, SQL Server, in-memory/Testing) duplicate business rules and have a real correctness hole: `GetBlockedEventsOnSession` treats `take <= 0` as `int.MaxValue` (unbounded query) in **all three** providers, and the in-memory store never routes through `PaginationLimits` at all — so the conformance suite can't catch a provider violating the page cap it exists to guarantee. Additionally, the blocked-event `"self"` → `LastMessageId` mapping is triplicated (with a latent NRE in the Cosmos copy), the error-list formatting is triplicated, and `CosmosDbClient` (2,502 lines) / `SqlServerMessageStore` (1,637 lines) mix ~7 concerns each. This was item 1 of the refactoring audit; items 1–2 (ServiceBusManagement + dead bridges) already shipped in v2.0.0.

Grounding facts (verified):
- `PaginationLimits` (`src/NimBus.MessageStore.Abstractions/PaginationLimits.cs`): `Resolve(take)` → `<=0` gives Default=100, `>1000` gives 1000. Used by Cosmos/SQL search/paging methods but NOT by `GetBlockedEventsOnSession` (Cosmos `:857`, SQL `:891`, InMemory `:219` all do `take <= 0 ? int.MaxValue : take`), and not anywhere in `InMemoryMessageStore` (inline `100` literals instead).
- Sole production caller `EventImplementation.cs:738` already clamps take to [1,200] → store-level `Resolve()` is behavior-safe for the WebApp.
- `MessageStore.Abstractions` already references `NimBus.Core`, so shared helpers can use `Constants.Self`.
- `INimBusMessageStore` aggregates all six store interfaces; DI (`CosmosDbMessageStoreBuilderExtensions.cs:63-77`, `SqlServerMessageStoreBuilderExtensions.cs:57-73`) registers it and forwards granular interfaces from it. Carve-outs must be **composition behind the facade** — no DI or consumer changes.

## Changes (4 local commits, TDD each; no push until asked)

### Commit 1 — `refactor(messagestore): extract shared blocked-event and error-list rules`

New `src/NimBus.MessageStore.Abstractions/BlockedEventRules.cs`:
```csharp
public static class BlockedEventRules
{
    public static string ResolveOriginatingId(string? originatingMessageId, string? lastMessageId)
        => string.Equals(originatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase)
            ? lastMessageId ?? string.Empty
            : originatingMessageId ?? string.Empty;
}
```
New `src/NimBus.MessageStore.Abstractions/EndpointErrorListFormat.cs`: consts `FailedStatus`/`DeferredStatus` + `Format(IReadOnlyCollection<string> ids)` → `""` when empty, else `string.Join(";", ids) + ";"` (preserves the trailing-`;` shape all providers emit today).

Tests first (in `tests/NimBus.MessageStore.InMemory.Tests`, next to `PaginationLimitsTests`): `BlockedEventRulesTests` (self→last, self+null-last→empty, non-self passthrough, case-insensitive, null originating → empty) and `EndpointErrorListFormatTests`.

Replace call sites: `CosmosDbClient.cs:1204-1211` (`ToBlockedMessageEvent` — this **fixes the latent NRE** on null `OriginatingMessageId`; note in commit body), `SqlServerMessageStore.cs:942-955`, `InMemoryMessageStore.cs:640-652`; error-list formatting in all three `GetEndpointErrorList` implementations. Do NOT unify id shapes (Cosmos doc-id vs `eventId_sessionId`) or SQL's ordering — formatting/status-set only.

### Commit 2 — `fix(messagestore): enforce pagination caps via PaginationLimits everywhere`

Conformance tests first, in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`:
- `GetBlockedEventsOnSession_nonpositive_take_is_capped_at_default_page_size` — seed 101 events on one session; `take: 0` → `Items.Count == PaginationLimits.DefaultPageSize`, `Total == 101`; also `take: -1`.
- `GetBlockedEventsOnSession_take_above_max_is_capped` — seed 3; `take: MaxPageSize + 5` → 3 returned (proves the Resolve path, cheap on live emulators).
- 1001-seed max-clamp assertion goes **InMemory-only** (`tests/NimBus.MessageStore.InMemory.Tests`) — live-emulator seeding of 1001 docs buys no extra coverage over the 101-seed routing proof + existing `PaginationLimitsTests` arithmetic.

Production: replace the `int.MaxValue` fallback with `PaginationLimits.Resolve(take)` at Cosmos `:857`, SQL `:891`, InMemory `:219`; in `InMemoryMessageStore` swap the inline `100` literals for `PaginationLimits.Resolve(...)` in `GetEventsByFilter`, `DownloadEndpointStatePaging`, `SearchMessages`, `SearchAudits` (mirroring Cosmos/SQL).

Behavior note: `take<=0` now returns 100 instead of everything — a deliberate DoS-hardening fix; call out in the next release notes. Before committing, grep tests for `GetBlockedEventsOnSession(` with non-positive take (existing conformance tests use positive takes).

### Commit 3 — `refactor(cosmosdb): extract metrics, ACL and schema stores from CosmosDbClient`

Pure move, covered by existing + new conformance tests. New `internal sealed` classes in `src/NimBus.MessageStore.CosmosDb/`:
- `CosmosDbMetricsStore : IMetricsStore` — region `:2101-2400` incl. private query helpers + row DTOs.
- `CosmosDbAccessControlStore : IAccessControlStore` — region `:1269-1323` incl. read/upsert helpers.
- `CosmosDbEventSchemaStore : IEventSchemaStore` — region `:1324-~1435`.

Each ctor takes container-accessor delegates (`Func<Task<ICosmosContainerAdapter>>`) + optional logger, so the container cache / `EnsureContainerExistsAsync` / eviction semantics (`CosmosDbClient.cs:1218-1238`) stay in `CosmosDbClient` untouched. `CosmosDbClient` instantiates the three in its ctor and keeps its interface members as one-line delegations (facade keeps implementing `INimBusMessageStore`; DI unchanged).

### Commit 4 — `refactor(sqlserver): extract metrics, ACL and schema stores from SqlServerMessageStore`

New `internal sealed SqlServerStoreContext` (holds the `OpenAsync` delegate — match its exact return type at `SqlServerMessageStore.cs:45` — the `T()` quoting delegate, and command timeout). New `SqlServerMetricsStore` (`:1298-1511`), `SqlServerEventSchemaStore` (`:1512-1570`), `SqlServerAccessControlStore` (`:1571-1637`), each ctor `(SqlServerStoreContext)`. Facade delegates as in commit 3.

## Verification

1. `dotnet build src/NimBus.sln` — full solution; public surface unchanged, WebApp/OpenTelemetry decorator compile untouched.
2. `dotnet test tests/NimBus.MessageStore.InMemory.Tests` — conformance + new unit tests (runs everywhere incl. CI). New cap tests must FAIL before commit 2's production change and pass after.
3. `dotnet test tests/NimBus.Core.Tests tests/NimBus.Resolver.Tests tests/NimBus.WebApp.Tests` — consumers of the stores.
4. Optional live conformance: start the Cosmos vnext + SQL emulator containers per the docker recipe in project memory, then run `tests/NimBus.MessageStore.CosmosDb.Tests` and `tests/NimBus.MessageStore.SqlServer.Tests` (env-gated).
5. Final greps: no remaining `"self"` literals or `int.MaxValue : take` under `src/`.

Also: copy this plan to `docs/superpowers/plans/` in commit 1 per repo convention.

## Out of scope (explicitly)

Unifying `GetEndpointErrorList` id shapes/ordering; splitting the tracking half of the stores; any DI/registration changes; `ICosmosDbClient`-era compat (already deleted in v2.0.0); pushing/releasing (local commits only).
