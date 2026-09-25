# Failed messages page (WebApp)

Status: proposed (2026-09-25), not started.

Mockup: [Failed Messages Mockup](https://claude.ai/artifact/Hzao9DvhYDUTjmBg359kHo). It has
one interactive board with a time-range switch, a stacked failure histogram, filters shaped
like the Messages page, and List / By endpoint / By error views. The numbers in it are sample
data.

## Goal

Add a single WebApp page, **Failed** (`/Failed`), where an operator can find and act on every
unresolved failure across all endpoints they can read:

- It lists every event currently in `Failed`, `DeadLettered` or `Unsupported`, with the same
  columns as the endpoint-details Messages tab plus **Endpoint** and **Last error**.
- It offers filters like the Messages page: endpoint, event type, event ID, last message ID,
  session ID, publisher (From), subscriber (To) and error text, plus status toggles.
- Above the list, a **stacked bar chart** shows failed events per time bucket, split by status
  or by endpoint. The bucket size follows the chosen range: 5 minutes, 30 minutes, an hour, 3
  hours, 6 hours or a day. Clicking a bar narrows the list to that window.
- It supports the same bulk Resubmit / Skip and Reported actions as the endpoint page.

## Non-goals

- Replaying failures from message history. The **Metrics** page already charts
  `ErrorResponse` messages over time. This page shows the *current* failure backlog.
- Payload search across endpoints. That stays on the endpoint page, which enforces
  `PayloadSearchPolicy` per endpoint (Decision 6).
- Server-side error grouping across the whole result set. "By error" groups the loaded rows,
  the same way the endpoint page does today (Decision 9).
- Changes to the Resolver, SDK or Service Bus topology.

## What exists today (verified 2026-09-25 on `7e4860d5`)

- **Event search is per endpoint.** `POST /api/event/{endpointId}/getByFilter`
  (`src/NimBus.WebApp/api-spec.yaml`, implemented in
  `Controllers/ApiContract/EventImplementation.Search.cs`) forces
  `filter.EndPointId = endpointId`. It checks `HasRoleAsync(AccessRole.Reader, endpointId)`,
  writes a `SearchEvents` audit row, and attaches resubmit counts and report flags per
  endpoint.
- **Storage.** `IMessageTrackingStore.GetEventsByFilter(EventFilter, continuationToken, max)`.
  - **SQL Server** and **in-memory** already treat a null `EndPointId` as all endpoints: one
    table, `ORDER BY UpdatedAtUtc DESC, Id DESC`, and SQL pages by an offset token.
  - **Cosmos DB** keeps **one container per endpoint**
    (`_getEndpointContainer(filter.EndPointId)` in `CosmosDbMessageTrackingStore.Search.cs`),
    so a search across endpoints has to fan out over the containers.
- **Metrics.** `IMetricsStore` (Cosmos and SQL only, with **no in-memory implementation and no
  OTel decorator**) buckets the *messages* container by `SUBSTRING(EnqueuedTimeUtc, 0, n)`, with
  `Period` mapped to 16/13/10 characters in `MetricsImplementation.PeriodToBucketConfig`. That
  is message history, not current event state, so it cannot supply this chart.
- **Error text.** Search results keep `MessageContent.ErrorContent` and strip only
  `EventContent.EventJson` (contract comment in `IMessageTrackingStore.cs`).
  `error-grouped-view.tsx` groups rows on `messageContent.errorContent.errorText` with
  `normalizeErrorPattern`. `PayloadRedaction` masks only `EventJson`, so a Reader can already
  see error text.
- **"N blocked" chip.** `events-panel.tsx` calls `postEndpointSessionsBatch` for the loaded
  rows' sessions and shows `deferredCount` next to failed rows. The call is scoped to one
  endpoint.
- **Resubmit / Skip.** `POST /api/event/resubmit/{eventId}/{messageId}` and
  `/api/event/skip/{eventId}/{messageId}` do not take an endpoint, so bulk actions can use them
  unchanged.
- **Counts for every endpoint.** `GET /api/endpoint/status/count` returns
  `failedCount` / `deadletterCount` / `unsupportedCount` for each endpoint. The nav badge can
  use it.
- **Chart library.** `recharts` ^3.7 is already a ClientApp dependency (used by Metrics).

## Decisions

1. **The chart shows the current backlog, bucketed by `UpdatedAt`.** Each bar counts events
   that are *still* Failed/DeadLettered/Unsupported, placed at the time they last entered that
   state. Chart and list then answer the same question, so clicking a bar is an exact filter
   (`UpdatedAtFrom/To`). Resolved failures drop out of the chart, and the Metrics page covers
   failure history.
2. **The new storage members go on `IMessageTrackingStore`, not `IMetricsStore`.** Event state
   lives in the tracking store, and it is the interface that has in-memory, conformance and OTel
   decorator coverage (AGENTS.md: a new storage member must be implemented in every provider,
   covered by the conformance suite, and forwarded by
   `InstrumentingMessageTrackingStoreDecorator`).
3. **Reuse `EventFilter`, add two fields and an explicit endpoint scope.**
   - `EventFilter.ErrorText` (string?): case-insensitive *contains* on `ErrorContent.ErrorText`.
   - `EventFilter.LastMessageId` (string?): case-insensitive *prefix*, the same as the other
     ID fields.

   Both are also honoured by the existing `GetEventsByFilter` in all three providers. Each
   provider's predicate building moves into one private helper shared by both methods, so the
   filters cannot drift apart. The endpoint set is passed as a separate argument that the WebApp
   has already authorized. `EventFilter.EndPointId` stays a prefix filter, as it is today.
4. **Search across endpoints: one method, a different paging strategy per provider.** The
   continuation token is opaque, as it is today.
   - SQL Server: one query with `EndpointId IN @EndpointIds`, the existing `ORDER BY` and the
     existing offset token.
   - In-memory: LINQ over the same predicate.
   - Cosmos DB: fan out to each endpoint container in parallel (bounded, e.g. 8 at a time). Each
     query is `ORDER BY c.event.UpdatedAt DESC` with `TOP pageSize + 1`. The results are merged
     in memory by `(UpdatedAt desc, id desc)`. The token is a global **keyset cursor**
     `(UpdatedAt, endpointId, id)`, base64 JSON. The next page asks each container for
     `UpdatedAt <= cursor.UpdatedAt` and drops rows at or before the cursor in the merged order.
     A missing container (`EndpointNotFoundException` or 404) is skipped, not fatal.
     - Known limit: if more than `pageSize` rows in one container share the cursor's exact
       `UpdatedAt` tick, the page can come back short. That is acceptable at tick resolution.
       The conformance suite pins the ordering contract, not this edge.
5. **Histogram: one new method; SQL aggregates, Cosmos buckets in the app.**
   - `GetFailedEventHistogram(EventFilter filter, IReadOnlyCollection<string> endpointIds,
     DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize)` returns sparse rows
     `(DateTime BucketStartUtc, string EndpointId, string Status, int Count)` plus a
     `Truncated` flag.
   - Buckets are aligned to `fromUtc`. The WebApp aligns `fromUtc` to a bucket boundary and
     fills empty buckets with zero.
   - SQL: `GROUP BY` the bucket index
     (`DATEDIFF_BIG(second, @From, UpdatedAtUtc) / @BucketSeconds`), `EndpointId`, `Status`,
     using the shared predicate.
   - Cosmos: run the same LINQ predicate projected to `{ UpdatedAt, Status }` in each
     container, and bucket in memory. Failure backlogs are small; a cap (default 50,000 rows
     across all containers) sets `Truncated` instead of scanning without limit. This honours
     every filter without a second hand-written SQL dialect.
   - In-memory: LINQ.
6. **Authorization and audit.**
   - The scope is every `platform.Endpoints` entry for which
     `HasRoleAsync(AccessRole.Reader, id)` holds.
   - If the request names endpoints, the scope is those names intersected with readable ones.
     Naming an endpoint the caller cannot read returns 403 and an audit row with
     `accessDenied: true`, the same as the endpoint route.
   - An empty readable set returns an empty result, not 403.
   - Each search writes one `SearchEvents` audit row, with the serialized request as `Data` and
     `endpointId: null`.
   - The request has **no payload field**, so `PayloadSearchPolicy` is not needed. Error text
     is already visible to Readers (see What exists today).
   - `payloadRedaction.Redact` still runs for callers who are not PiiReaders, as a
     defence-in-depth measure.
7. **Buckets per range.** The server picks the bucket size and returns it with the counts.

   | Range | Bucket | Bars |
   |---|---|---|
   | 1h | 5 min | 12 |
   | 12h | 30 min | 24 |
   | 1d | 1 h | 24 |
   | 3d | 3 h | 24 |
   | 7d | 6 h | 28 |
   | 30d | 1 d | 30 |
   | Custom from/to | smallest of {5m, 15m, 30m, 1h, 3h, 6h, 12h, 1d} giving ≤ 60 bars | ≤ 60 |

   Reuse the existing `Period` enum for the presets and add optional `from`/`to` for custom
   ranges.
8. **Status whitelist.** Accept only `Failed`, `DeadLettered` and `Unsupported`. Anything else
   returns 400. An empty list means all three.
9. **What the client derives.**
   - The "N blocked" chip: group the loaded rows by endpoint and call the existing
     `postEndpointSessionsBatch` once per endpoint, fail-soft as the endpoint page does.
   - Resubmit counts and report flags: attach them server-side, grouped by endpoint, reusing
     `AttachResubmitCounts` / `AttachReportFlags`.
   - "By endpoint" counts come from the histogram response, so they are exact for the whole
     scope. Its "most common error" comes from the loaded rows and is labelled that way.
   - "By error" reuses `ErrorGroupedView` over the loaded rows.
10. **Navigation.** Add a **Failed** item under OBSERVE, directly after Messages, with a badge
    showing the sum of the three counts from `GET /api/endpoint/status/count`. Link to it from
    the endpoint page's FAILED tile (`/Failed?endpoint=<id>`) and the Endpoints list.

## API contract (edit `api-spec.yaml`; NSwag regenerates both sides)

- `POST /api/failed/search` (tag `Event`, operationId `post-failed-search`)
  - Request `FailedSearchRequest`:
    - `endpointIds?: string[]`
    - `statuses?: ("Failed"|"DeadLettered"|"Unsupported")[]`
    - `eventTypeId?: string[]`
    - `eventId?`, `lastMessageId?`, `sessionId?`, `from?`, `to?`, `errorText?`
    - `updatedAtFrom?`, `updatedAtTo?` (date-time)
    - `continuationToken?`
    - `maxSearchItemsCount` (int)
  - Response: the existing `SearchResponse`. Every `Event` already carries `endpointId`.
- `GET /api/failed/histogram` (operationId `get-failed-histogram`)
  - Query: `period` (`Period`, optional), `from`/`to` (date-time, optional; they replace
    `period`), and the same filter fields as search, as repeatable query parameters.
  - Response `FailedHistogram`:
    - `bucketSize` (ISO-8601 duration string, e.g. `PT1H`)
    - `from`, `to`
    - `buckets: [{ start, failed, deadLettered, unsupported, byEndpoint: {<endpointId>: int} }]`
    - `totals: { failed, deadLettered, unsupported, byEndpoint: [{ endpointId, failed,
      deadLettered, unsupported }] }`
    - `truncated: bool`

  The histogram could be `POST` to share the search request body. Pick `POST
  /api/failed/histogram` with `FailedSearchRequest` plus `period`/`from`/`to` if the query
  string becomes awkward in NSwag. Decide when writing the spec, and prefer the shared body.

## Tasks

Write the failing test first in each task (RED → GREEN). Each numbered group is one reviewable
commit.

1. **Storage contract and conformance (RED).**
   - Add `ErrorText` and `LastMessageId` to `EventFilter`, with XML docs.
   - Add `GetFailedEventsAcrossEndpoints(EventFilter filter, IReadOnlyCollection<string>
     endpointIds, string? continuationToken, int maxItemCount)` and `GetFailedEventHistogram(…)`
     to `IMessageTrackingStore`, with XML docs. Add the histogram row/result types under
     `NimBus.MessageStore.Abstractions/States/`.
   - Add conformance cases to `NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`:
     - only the given endpoints are returned
     - only the three statuses come back, even if the filter asks for others (the provider
       ignores non-failure statuses)
     - ordering is `UpdatedAt desc`
     - paging through every page returns each row exactly once, with page size 2 over 7 rows
       across 3 endpoints
     - `ErrorText` contains, case-insensitive
     - `LastMessageId` prefix
     - histogram bucket placement at the bucket edges (inclusive start, exclusive end), the
       per-endpoint split, and empty buckets absent from the sparse result
     - `ErrorText` / `LastMessageId` also filter the existing `GetEventsByFilter`
2. **In-memory provider (GREEN for in-memory)** in `NimBus.Testing/Conformance/InMemoryMessageStore.cs`.
3. **SQL Server provider** in `SqlServerMessageTrackingStore.Search.cs`.
   - Extract the `where`/parameter builder, add `IN @EndpointIds`, `ErrorText` (`LIKE` on
     `MessageContentJson`, escaped, via `JSON_VALUE(... '$.ErrorContent.ErrorText')`) and
     `LastMessageId`.
   - Add the histogram query.
   - Use the `SqlConnection`-specific Dapper overloads (existing shim) so transient errors are
     still translated.
   - Run the live conformance tests with `NIMBUS_SQL_TEST_CONNECTION`.
4. **Cosmos DB provider** in `CosmosDbMessageTrackingStore.Search.cs`.
   - Extract the LINQ predicate helper and add the two filters. Check the property path
     (`x.Event.MessageContent.ErrorContent.ErrorText`) against a real document before relying
     on it.
   - Add the fan-out/merge search with the keyset cursor, and the capped projection histogram.
   - Unit-test the cursor encode/decode and the merge in `tests/NimBus.MessageStore.CosmosDb.Tests`.
   - Run the live conformance tests against the emulator (`NIMBUS_COSMOS_TEST_GATEWAY=1`).
5. **OTel decorator.** Forward both new members in `InstrumentingMessageTrackingStoreDecorator`.
   Add a decorator test in `tests/NimBus.OpenTelemetry.Tests` asserting the calls reach the
   inner store. An unforwarded default interface method would silently bypass it.
6. **API spec and controller.**
   - Add both operations and schemas to `api-spec.yaml`, and build with NSwag generation enabled
     (not `SkipSpaBuild=true`).
   - Implement them in a new `EventImplementation.Failed.cs` partial: authorization scope,
     audit, the status whitelist, bucket selection, zero-fill, and enrichment grouped by
     endpoint.
   - Add `tests/NimBus.WebApp.Tests/FailedMessagesEndpointTests.cs`:
     - the scope excludes endpoints the caller cannot read
     - an explicitly requested unreadable endpoint returns 403 plus a denied audit row
     - the status whitelist returns 400
     - one audit row per search
     - redaction for callers who are not PiiReaders
     - bucket-size selection for each preset and for custom ranges
     - zero-fill and alignment
     - totals match the sum of the buckets
7. **ClientApp page.**
   - Add `pages/failed-messages.tsx` and `components/failed-messages/` (filter bar, histogram,
     table).
   - Store filters in the URL via `useUrlFilters`, so links and Back work.
   - Reuse `DataTable`, the status badges, `ErrorGroupedView`, the report popover and the
     resubmit/skip helpers from `events-panel.tsx`. Extract shared pieces instead of copying
     them.
   - Build the histogram with recharts `BarChart` (stacked, custom tooltip) and select a bar on
     click. The status colours follow `index.css` (`status-danger` and its darker ink for
     DeadLettered, muted for Unsupported).
   - Add the route in `app.tsx` and the sidebar item and badge in `components/sidebar.tsx`.
   - Add the FAILED-tile link on the endpoint page.
8. **Frontend tests** (Vitest, `npm run test:ci`):
   - the filter ↔ URL round-trip
   - clicking a bar sets `updatedAtFrom/To`, and the chip clears it
   - status toggles restyle the chart and refetch
   - the endpoint column links to `/Endpoints/Details/<id>`
   - the blocked chip calls the session batch once per endpoint
   - the badge sums the counts
   - the empty state
9. **Docs and screenshot.** Add a short section to the WebApp guide in `docs/`. Put a screenshot
   of the finished page in the PR description, using demo data (public repo).

## Verification gate

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
```

- Run the live SQL Server and Cosmos DB conformance suites locally, with the Docker recipe
  from project memory or the CI secrets. CI rejects skipped conformance tests.
- Manual check: run the Aspire stack (`dotnet run --project src/NimBus.AppHost`) with the
  simulator's failure modes to produce Failed, DeadLettered and Unsupported events on at least
  three endpoints. Then check that:
  - the chart totals match the endpoint tiles
  - clicking a bar narrows the list exactly
  - paging returns no duplicates
  - bulk resubmit clears rows

## Risks and open questions

- **Cosmos fan-out cost.** Each page reads up to `endpoints × (pageSize + 1)` documents.
  That is fine for tens of endpoints. If a deployment has hundreds, add a server-side cap on
  concurrent container queries and consider requiring an endpoint filter above a threshold.
- **`ErrorText` on Cosmos** is a `CONTAINS` scan within each container. The status predicate
  narrows it first, but it is still unindexed. Measure it on `rg-nbdemo-dev`-sized data before
  release.
- **`UpdatedAt` semantics.** Confirm that every path into Failed, DeadLettered or Unsupported
  (Resolver, dead-letter replay, handoff fail) stamps `UpdatedAt`. If one does not, the chart
  puts those events in the wrong bucket. Add a conformance assertion if a gap turns up.
- **Versioning.** The additions are additive (new interface members with no default
  implementation). External `IMessageTrackingStore` implementers must add them, which the
  v4.0.0 breaking-change ledger (`docs/plan/2026-09-24-v4-code-quality.md`) should record if
  this ships in v4. Otherwise it goes in the next minor's release notes.
