# Performance Improvements — Handover Guide

This document describes the performance sweep applied to NimBus on 2026-07-02
(commits `87309d6..34ce95e`), written so the same improvements can be ported to a
sister solution with the same architecture: Azure Service Bus transport, a message
tracking store (Cosmos DB and/or SQL Server), a transactional SQL outbox, and a
React management WebApp polling that store.

Each section states the **problem**, the **change**, **how to port it**, and the
**gotchas we hit** — the gotchas are the valuable part; the changes themselves are
mostly small. Reference commits and files point at the NimBus implementation.

## Summary

| # | Improvement | Layer | Breaking? | Commit |
|---|-------------|-------|-----------|--------|
| 1 | Suppress write content echo, parallelize aggregates, bound reads | Cosmos store | No | `87309d6` |
| 2 | Cache health-check account read (30s, single-flight) | Cosmos store | No | `195eff3` |
| 3 | Prefix ID search (index-served) + payload-free search projections | All store providers | **Yes** (search semantics) | `b15dd77` |
| 4 | Multi-row INSERT for outbox batch store | SQL outbox | No | `27cb9f0` |
| 5 | Serialize once on batch publish | SDK/publisher | No (new API, old kept) | `4e72af5` |
| 6 | Configurable `MaxConcurrentCalls` for deferred processor | SDK/hosting | No (opt-in) | `e30b331` |
| 7 | Short-TTL store-result cache for status counts & metrics | WebApp backend | No | `8051abf` |
| 8 | Map event types once per endpoint-details request | WebApp backend | No | `72bc79f` |
| 9 | Evict API client + moment from the entry bundle | WebApp frontend | No | `1a47ad2` |
| 10 | Pause polling while the tab is hidden | WebApp frontend | No | `ebb12f4` |
| 11 | Parallel mount fetches; memoize API client | WebApp frontend | No | `e72ae04`, `04f0964` |
| 12 | Don't cache a rejected in-flight request | WebApp frontend | No (bug fix) | `34ce95e` |

---

## 1. Cosmos DB store hot path (`87309d6`)

Three independent fixes to the Cosmos client, all low-risk:

**Suppress the write content echo.** By default every Cosmos upsert response
echoes the *entire document* back over the wire — including the message payload.
The hot-path writes (message upload, completed-message upload, message store,
audit store) only ever read `StatusCode` from the response. Set
`ItemRequestOptions.EnableContentResponseOnWrite = false` on those calls.

*Porting:* grep your Cosmos client for `UpsertItemAsync`/`CreateItemAsync` calls
whose response is only checked for status; add the option there. Do **not** apply
it blindly to calls that read the returned document (e.g. ETag-based flows).

**Parallelize independent aggregate queries.** Metrics endpoints
(overview/latency/time-series) and the blocked-events query each ran 2–3
independent Cosmos queries sequentially. Wrap them in `Task.WhenAll` — the
latency becomes one round-trip instead of the sum.

*Porting:* only parallelize queries that are truly independent (no shared
continuation state, no ordering dependency). Keep per-query error semantics.

**Bound unbounded reads.** Event-history and audit reads used unbounded
`MaxItemCount` (Cosmos default −1 pulls large pages). Set explicit page bounds
(NimBus: 100 for event history, 1000 for audits) — the drain loops still read
every match, but memory and per-page latency are bounded.

Reference: `src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs`.

## 2. Health-check caching (`195eff3`)

**Problem:** every `/ready` probe paid a `ReadAccountAsync` round-trip to Cosmos.
With Kubernetes-style probing that is a constant background RU + latency tax.

**Change:** cache the health result — *healthy and unhealthy alike* — for 30
seconds, with single-flight refresh so concurrent probes share one account read.
Time is taken from an injectable `TimeProvider` so tests can fast-forward.

**Gotcha (load-bearing):** `AddCheck<T>()` activates a **fresh instance per
probe**, so an instance-level cache never hits. You must register the health
check as a singleton (`AddSingleton<CosmosDbHealthCheck>()`) and let the health
system resolve that instance. If your DI wiring registers checks by type only,
this change silently does nothing.

Reference: `src/NimBus.MessageStore.CosmosDb/HealthChecks/CosmosDbHealthCheck.cs`,
`HealthCheckExtensions.cs`.

## 3. Prefix ID search + payload-free projections (`b15dd77`) — BREAKING

The biggest store change, in two halves. Verified green against a live Cosmos
emulator (63/63), a SQL Server container (49/49), and in-memory (55/55).

### 3a. Prefix matching on ID-like search fields (breaking semantics)

**Problem:** Cosmos search filters used `CONTAINS(LOWER(...))` on ID-like fields
(endpoint id, event id, message id, session id, auditor name, event type id).
`LOWER()` defeats the index, so every search was a **full container scan** —
cost grows linearly with data volume.

**Change:** switch all three providers to **case-insensitive prefix** matching:

- Cosmos: `STARTSWITH(c.field, @value, true)` — served by the range index.
- SQL Server: `field LIKE @value + '%' ESCAPE '\'` with a `LikePrefix` helper
  that escapes `%`, `_`, `[`, `\` in user input; relies on a CI collation.
- In-memory/testing provider: `StartsWith(OrdinalIgnoreCase)`.

**This changes user-visible behavior** — decide consciously per provider:

- Cosmos *tightens*: mid-string fragments no longer match (`"abc"` no longer
  finds `"xx-abc-yy"`). Users who search by pasting an ID fragment from the
  middle will notice.
- SQL *loosens*: these fields were exact-match before; now prefixes match.
- Free-text fields (To/From/Payload) are deliberately unchanged.

Pin the new semantics with conformance tests: exact hit, uppercase hit,
mid-fragment **miss**.

### 3b. Payload-free search projections

**Problem:** search/list pages selected `*`, so every result row carried the full
event payload (`EventJson`) over the wire — for a list view that never shows it.

**Change:** project server-side. Cosmos uses LINQ member-init projections
(`GetEventsByFilter` selects every property *except* `EventJson`; error content
kept because the UI shows it) and a named `MessageSearchProjection` for message
search instead of `SELECT *`. SQL mirrors with explicit column lists; the
in-memory provider clones-without-payload so stored instances stay intact.

**Gotcha:** projections silently drift when someone adds a property to the
entity later. Add a **reflection drift guard** test per projection: enumerate the
entity's properties, assert each one (minus the intended exclusions) is populated
by the projection. Two such guards exist in
`tests/NimBus.MessageStore.CosmosDb.Tests/MessageSearchProjectionTests.cs`.

### 3c. Latent Cosmos read bugs — found only by running the gated suite live

Running the previously-skipped Cosmos conformance suite against a real emulator
surfaced two long-standing bugs the in-memory suite could never catch:

- Reads never hydrated `ResolutionStatus` from the stored `c.status` field — it
  was always the enum default on every Cosmos read.
- Audit search queried lowercase JSON paths (`c.audit.auditorName`) against
  PascalCase-serialized documents, and compared a numeric enum to a string —
  auditor/audit-type filters **never matched** on Cosmos.

**Lesson for the sister solution:** if your provider conformance tests are
env-gated and CI never runs them against a live emulator, assume similar bugs
exist. Run the suite live once before porting anything else (see
`tests/NimBus.MessageStore.CosmosDb.Tests` for the emulator gating pattern).

## 4. Multi-row outbox INSERT (`27cb9f0`)

**Problem:** `StoreBatchAsync` issued one INSERT round-trip per message.

**Change:** build one multi-row `INSERT ... VALUES (...),(...)` statement per
chunk of 100 messages, with per-row suffixed parameters (`@Id0`, `@Id1`, …). The
chunk size is deliberate: 12 parameters/row × 100 = 1,200, safely under SQL
Server's **2,100 parameters-per-command limit**, and it matches the existing
`MarkAsDispatchedAsync` batch size. A shared `CreateBatchInsertCommand` builder
serves both the ambient-`TransactionScope` path and the owned-transaction path;
empty input returns early.

*Porting math:* your chunk size = `floor(2100 / columns-per-row)` with headroom.
If your outbox row has 20 columns, cap at ~100 rows anyway for plan-cache sanity.

*Testing pattern worth copying:* DB-free unit tests assert the generated
`CommandText` and parameter set exactly; a connection-string-gated integration
suite covers 1/100/101/250 rows, duplicate-id atomic rollback, and ambient
commit/rollback. Reference: `src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs`,
`tests/NimBus.Outbox.SqlServer.Tests/`.

## 5. Serialize once on batch publish (`4e72af5`)

**Problem:** batch publishing serialized each event **4–6 times**: a validation
probe, a sizing probe that built full `ServiceBusMessage` instances (with
`Body.ToArray()` copies) per page attempt, then again at actual send.

**Change:** a new preferred API, `IPublisherClient.PublishBatches`, that:

1. Builds each message object once and serializes the body **once**.
2. Uses the serialized length for greedy first-fit paging against a hoisted
   `MaxBatchBodyBytes` budget (no `ServiceBusMessage` materialization for
   sizing; per-event measurement carried across page boundaries).
3. Stashes the bytes on the message (`Message.SerializedMessageContent`) so the
   transport layer sends them verbatim instead of re-serializing.

The old API stays; the interface method has a default implementation (DIM) so
existing fakes/implementations keep compiling.

**Gotcha (mandatory):** the stash property **must be `[JsonIgnore]`**. The outbox
decorator serializes the whole `Message` object into the outbox `Payload` column
— without the attribute every outboxed message would embed its own serialized
copy of itself. This is pinned by a guard test; port the test with the feature.

Contracts preserved and pinned by tests: an oversized single item still goes out
as its own single-item batch; input-list drain order is unchanged.

Reference: `src/NimBus.SDK/PublisherClient.cs`, `src/NimBus.Core/Messages/Models/Message.cs`,
`tests/NimBus.SDK.Tests/PublisherClientBatchTests.cs`.

## 6. Deferred processor concurrency (`e30b331`)

`AddNimBusDeferredProcessorHostedService` gains an optional, validated
`maxConcurrentCalls` parameter (default stays 1). Small change; the reason it
needs care: the deferred trigger subscription is **non-session**, so
`MaxConcurrentCalls = 1` is its *only* ordering mechanism. Raising it trades
ordered replay for throughput — say so in the XML docs so consumers opt in with
eyes open, and keep the default at 1.

Reference: `src/NimBus.SDK/Hosting/DeferredMessageProcessorHostedServiceOptions.cs`.

## 7. Short-TTL store-result cache in the WebApp (`8051abf`)

**Problem:** the Monitor wall polls status counts for every endpoint every few
seconds, per open browser tab; the Insights page re-runs metrics aggregates on
every visit. Each poll hit the store directly — N tabs × M endpoints round-trips.

**Change:** a small `IStoreResultCache` service (`IMemoryCache` +
`Lazy<Task<T>>` for single-flight) wrapped around exactly two read paths:

- Endpoint status counts: **5s TTL**, keyed per endpoint. Monitor polling now
  costs at most one store round-trip per endpoint per 5s window regardless of
  tab count.
- Metrics overview/latency/time-series aggregates: **30s TTL**, keyed per period.

The core of the implementation (from `src/NimBus.WebApp/Services/StoreResultCache.cs`):

```csharp
public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
{
    Lazy<Task<T>> lazy;
    lock (_creationLock)
    {
        lazy = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return new Lazy<Task<T>>(factory);   // single-flight
        });
    }
    try { return await lazy.Value; }
    catch { _cache.Remove(key); throw; }         // never cache failures
}
```

Design decisions that matter when porting:

- **Targeted service, not a store decorator.** Operational reads (e.g. the
  resubmit path deciding whether an event is still failed) must always see live
  data. Callers opt in per call site; nothing is cached implicitly.
- **Cache below the authorization layer.** The status-count cache sits *under*
  the per-user `IsManagerOfEndpoint` filtering, so keys identify data (endpoint
  id, period) — never the user — and cached values contain no per-user
  filtering. Caching above auth is a data-leak bug.
- **Never cache faults.** A faulted factory task is evicted immediately; the
  exception propagates to every awaiting caller, and the next caller retries.
- **Register as a singleton.** The consuming controllers are transient; a
  per-request cache instance never hits.

## 8. Map event types once per request (`72bc79f`)

**Problem:** the endpoint-details endpoint mapped every event type twice (once
for the details list, once for namespace groupings) and re-probed
producer/consumer relationships on each mapping.

**Change:** build the consumed/produced detail objects once, derive both the
groupings and the flat list from them. Pure restructuring — but the ordering was
implicitly load-bearing for the UI, so regression tests were written **against
the original implementation first** (consumed-then-produced order,
first-occurrence namespace grouping, both-direction duplicates, 404 case), then
the refactor was made under them.

Also documented in-code during this pass: the two resubmit paths deliberately
publish **before** archiving — `ArchiveFailedEvent` soft-deletes, so a failed
publish must leave the event visible. Do not "optimize" that pair into
`Task.WhenAll` in either codebase.

## 9. Frontend: evict the API client + moment from the entry bundle (`1a47ad2`)

**Problem:** two eager import chains (the app-status hook and the command-palette
search) pulled the generated API client — and through it `moment` — into the
Vite entry chunk. First paint paid for 190 kB of API client nobody had called yet.

**Change:**

- Switch the two chains to `import type` for typings plus a dynamic
  `await import('./api-client')` at call time.
- Keep the in-flight-dedup assignment (`pendingRequest = ...`) **synchronous** —
  if the dynamic import is awaited before the pending-promise is stored, two
  concurrent callers both fire the request and dedup silently breaks.
- Pin the chunk name via Vite `manualChunks: { api: [...] }` so the split chunk
  keeps a stable name/hash across deploys (long-term caching).

Measured: entry chunk 239.1 kB (47.5 kB gzip) → 56.5 kB (17.6 kB gzip); the
190.7 kB `api` chunk loads on first API call instead of first paint.

## 10. Frontend: pause polling on hidden tabs (`ebb12f4`)

**Problem:** the Monitor page polls every 5s even when the tab is backgrounded —
multiplied by the store round-trips behind each poll (partly mitigated by §7,
but the cheapest request is the one not sent).

**Change:** skip poll ticks while `document.hidden`; on
`visibilitychange → visible`, refresh immediately.

**Gotcha:** staleness/connection-lost detection must measure from
`max(lastRefreshAt, resumedAt)`, not `lastRefreshAt` alone — otherwise a tab
resumed after minutes hidden flashes the "connection lost" banner while its
catch-up fetch is still in flight. Real failures still surface after 30s of
*visible* failing polls. Reference: `src/NimBus.WebApp/ClientApp/src/hooks/use-monitor-data.ts`
(+ its test file for the visibility simulation pattern).

## 11. Frontend: small mount-path wins (`e72ae04`, `04f0964`)

- Fetch the endpoint list and app status in **parallel** on the endpoints-page
  mount (`Promise.all`) instead of sequentially.
- Memoize the generated API client (`useMemo`) in components that constructed a
  new client instance on every render.

Both are five-line changes; grep the sister codebase for `new ApiClient(` inside
component bodies and for sequential `await`s in mount effects.

## 12. Frontend: never pin a rejected promise as "the cached request" (`34ce95e`)

**Bug pattern worth auditing for anywhere request-dedup is hand-rolled:** the
app-status hook stored the in-flight promise in `pendingRequest` for dedup, but
only cleared it on success. A single transient failure left the **rejected**
promise pinned, so every later call rethrew the stale error until a full page
reload.

**Fix:** clear `pendingRequest` in `finally` (success and failure); keep setting
the durable `cachedStatus` only on success so the next call retries.

---

## Cross-cutting lessons

1. **Run the env-gated provider suites live before optimizing.** The Cosmos
   conformance suite had never run against a real emulator; running it found two
   real correctness bugs (§3c) that in-memory tests structurally cannot catch.
2. **Pin behavior with regression tests before refactoring hot paths.** The
   event-type mapping (§8) and batch-paging contracts (§5) were pinned against
   the *original* implementation first, so the perf change couldn't silently
   change semantics.
3. **Caches need three explicit decisions:** where they sit relative to
   authorization (below it, user-free keys — §7), what happens to faults (never
   cached — §2, §7, §12), and lifetime/registration (singleton, or the cache is
   decorative — §2, §7).
4. **Serialization is the hidden hot loop** in message platforms: content echoes
   on writes (§1), `SELECT *` on list views (§3b), and repeated body
   serialization on publish (§5) all move payload bytes nobody reads.
5. **Declare breaking search semantics loudly** (§3a) — index-served search is a
   behavior change, not just a speedup; conformance tests must pin the miss
   cases, not only the hits.
