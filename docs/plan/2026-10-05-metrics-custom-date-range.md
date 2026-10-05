# Metrics page: custom date range

Status: implemented (2026-10-05) on branch `claude/metrics-custom-range`.

## Implementation notes

What shipped differs from the plan below in these places:

- **The `application\json` typo on `/api/metrics/latency` stays.** The generated client parses
  that response today, and changing the content type would change generated code for no gain
  in this change.
- **Obsolete members on the providers too.** Besides the interface, the providers' and the
  aggregate stores' `from`-only members carry `[Obsolete]`. That keeps a direct call through a
  concrete type warning like a call through the interface, and lets the obsolete forwarders call
  each other without CS0618.
- **Bucket keys stop before `to`.** The zero-fill now lists buckets that start before `to`
  (exclusive). Before, the fill ran up to and including "now".
- **Presets end at request time.** A preset now passes `to = now`, so a message whose
  `EnqueuedTimeUtc` is a few seconds ahead of the WebApp's clock waits for the next refresh. The
  30-second cache already delays it about as much.
- **Live conformance.** The SQL Server and Cosmos metrics suites ran against local containers
  (SQL Server 2022 and the vNext emulator). Cosmos ran twice against the same database to check
  that tests don't leak into each other.

## Context

PR #195 adds an Application Insights-style custom date range to the Failed page. The Metrics
page needed the same, shipped as a separate PR from master.

Unlike the Failed page, the backend could not do this:
- The five `/api/metrics/*` GETs took only `period`.
- `IMetricsStore` took only `DateTime from`. Every provider filtered `EnqueuedTimeUtc >= from`,
  and the time series zero-filled up to `DateTime.UtcNow`.

## Approach

### 1. Storage contract: non-breaking `(from, to)` overloads

Per `docs/versioning.md`, this is a minor change: additions plus a deprecation.

`IMetricsStore` gains `(from, to)` overloads of all five queries, with `to` exclusive:
- They are default interface methods that fall back to the `from`-only members, ignoring `to`,
  so third-party implementations keep compiling. Every NimBus provider overrides them, and the
  conformance suite fails a provider that ignores `to`.
- The `from`-only members are `[Obsolete]`, to be removed in v5. NimBus providers forward them
  to the new overloads with `to = DateTime.UtcNow`.

Providers:
- **SQL Server** adds `AND EnqueuedTimeUtc < @To`.
- **Cosmos** adds `AND c.message.EnqueuedTimeUtc < @to`, as the same exact ISO string `@from`
  uses. `CosmosDateBounds` widening doesn't apply: these are server-side GROUP BY aggregates
  with no rows to re-filter.
- **In-memory** adds `&& m.EnqueuedTimeUtc < to`.
- The time series zero-fills up to `to`.

### 2. Conformance suite

- The existing tests use the `(from, to)` overloads.
- Each query gains an upper-bound test: a message just before `to` counts, and messages at or
  after `to` don't.
- The time series must zero-fill exactly the buckets in the window.

### 3. API

The five `/api/metrics/*` GETs take optional `from` / `to` query parameters (`date-time`), with
`period` still required as the fallback. `MetricsImplementation.ResolveWindow`:
- Returns 400 for a lone bound, `from >= to`, or a span over 90 days (the same 90-day limit as
  the Failed page).
- Reads Local and Unspecified kinds as UTC.
- Buckets custom ranges by minute up to 2 hours, by hour up to 14 days and by day beyond.
- Keys the cache by the custom bounds; preset keys are unchanged.

### 4. Metrics page

- **Custom…** joins the period switcher. It opens local start/end fields with Apply/Cancel and
  validation; presets stay one-click and clear the range.
- Every card's label shows the range.
- `components/metrics/custom-range-popover.tsx` repeats #195's range validation on purpose,
  since this PR is independent of it. Deduplicating is a follow-up once both merge.

## Verification

- **Store:** the upper-bound conformance tests fail on in-memory before the change (RED), and
  pass on in-memory, SQL Server and Cosmos after it.
- **API:** `MetricsImplementationTests` covers presets, bucket choice, refused windows, UTC
  reading and cache keys. A TestServer test binds `from`/`to` from the query string.
- **Frontend:** Vitest covers the popover and the page passing the range to all four requests.
- **Gates:** `dotnet build src/NimBus.sln -c Release`, `dotnet test src/NimBus.sln -c Release
  --no-build`, `npm run test:ci` and `npm run build`.
