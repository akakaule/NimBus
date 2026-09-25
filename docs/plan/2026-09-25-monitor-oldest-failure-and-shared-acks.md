# Monitor: oldest-failure time and shared acknowledgements

Status: implemented (2026-09-25) on branch `claude/pensive-chaum-2aad31`.

Implementation notes: the in-memory store stamps `UpdatedAt` on write while SQL Server and
Cosmos keep the caller's value, so the conformance test orders uploads in real time and
compares against the stored values instead of back-dating them. ACK and clear are also
audited (`MessageAuditType.AcknowledgeEndpoint` / `ClearEndpointAcknowledgement`, appended
last), matching the other endpoint-level operator actions. The status API and the ACK policy
share one cached status-count helper (`EndpointStateCountCache`).

## Goal

The `/Monitor` wall display has two gaps that need backend work:

1. **Oldest failure time.** `EndpointStatusCount.eventTime` is only the query time (every
   store sets `EventTime = DateTime.UtcNow`), so the per-card "First seen …" and the header
   "T+ first failure seen" count from when the page first saw a failure and reset on reload.
2. **Shared acknowledgements.** ACKs live in the browser's `localStorage`
   (`nb.monitor.acks.v1`), so an ACK on a laptop does not silence the wall PC.

## Part 1 — oldest failure per endpoint

### Definition

`OldestFailureAt` = the earliest `UpdatedAt` of a non-deleted message on the endpoint whose
status is **Failed or DeadLettered**, or null when there is none. Both statuses count because
the Mapper folds dead-letters into `FailedCount` (`FailedCount + DeadletterCount`), which is
what the Monitor calls "failing".

`UpdatedAt` (not `EnqueuedTimeUtc`) is the right clock: it is when the record entered its
current status. A message that sat deferred for a day and then failed has been *failing* since
it failed, not since it was enqueued. When the oldest failure is resubmitted or skipped, the
value moves forward to the next-oldest open failure — the card reads "failing for" the
longest-standing open failure, which is what an operator needs.

### Changes

| Layer | Change |
| --- | --- |
| `EndpointStateCount` | New `DateTime? OldestFailureAt` (UTC). `EventTime` unchanged. |
| Cosmos | Extend the existing `GROUP BY c.status` count query with `MIN(c.event.UpdatedAt)`; take the minimum over Failed + DeadLettered groups. No extra round trip. |
| SQL Server | Same: `MIN(UpdatedAtUtc)` in the existing `GROUP BY Status` query (served by `IX_UnresolvedEvents_EndpointId_UpdatedAtUtc`). `DateTimeKind` normalized to UTC. |
| In-memory | Minimum `UpdatedAt` over the same events the counts use. |
| Decorator | `InstrumentingMessageTrackingStoreDecorator.DownloadEndpointStateCount` already forwards the whole object; no signature change. A decorator test pins that the new field survives. |
| Conformance | `DownloadEndpointStateCount_reports_oldest_open_failure`: Failed + DeadLettered + Pending + a *deleted* Failed row; asserts the oldest non-deleted Failed/DeadLettered `UpdatedAt` (tick tolerance for SQL) and null for an endpoint with only pending work. |
| API spec | `EndpointStatusCount.oldestFailureAt` (`date-time`, nullable). NSwag regenerates the contract and TS client. |
| Mapper | `OldestFailureAt = state.OldestFailureAt`. The storage-unavailable stub leaves it null. |
| Hook | `firstFailureAt` = server `oldestFailureAt` when present; falls back to the page-observed transition time only when the server sends none. |
| UI | Card: "Failing for 3h 20m"; header: "T+ since first failure". |

## Part 2 — shared acknowledgements

### Semantics (unchanged from today)

- An ACK carries `reason` (free text), `ackedAt` and `failedAtAck`.
- It expires 4 h after `ackedAt`.
- It clears automatically when the endpoint recovers (failed count reaches 0 after an ACK taken
  while failing).
- Anyone may clear it manually.

### Storage contract

New `IEndpointAcknowledgementStore` in `NimBus.MessageStore.Abstractions`, implemented by every
provider and added to the `INimBusMessageStore` aggregate (same shape as `IServiceHealthStore`
and `IAccessControlStore`):

```csharp
Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements();
Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement);   // upsert by EndpointId
Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null);
```

`EndpointAcknowledgement`: `EndpointId`, `AcknowledgementId` (a GUID minted per ACK — the
concurrency token), `Reason`, `AcknowledgedBy`, `AcknowledgedAtUtc`, `ExpiresAtUtc`,
`FailedCountAtAcknowledgement`.

`RemoveEndpointAcknowledgement` with `expectedAcknowledgementId` is a compare-and-delete: it only
removes the row if it is still the same ACK. The lazy clean-up below uses it so a clean-up based
on a stale read can never delete a newer ACK someone just placed. A GUID token rather than the
timestamp avoids the legacy-`DATETIME` parameter rounding SqlClient applies to client-generated
`DateTime` values.

| Provider | Storage |
| --- | --- |
| Cosmos DB | New `endpointacknowledgements` container (`/id`, id = endpoint id), created on first use like the other store containers; the id is added to `CosmosContainerDefaults.ReservedContainerIds`. Compare-and-delete = read, compare token, delete with `IfMatchEtag`. |
| SQL Server | Migration `0020_EndpointAcknowledgements.sql` (typed columns, PK `EndpointId`); table added to `SqlServerSchemaInitializer.RequiredTables`. Compare-and-delete = `DELETE … WHERE EndpointId = @id AND AcknowledgementId = @token`. |
| In-memory | `ConcurrentDictionary`; compare-and-delete via `TryRemove(KeyValuePair)`. |

Conformance: new `EndpointAcknowledgementStoreConformanceTests` (round-trip, upsert replaces,
list, remove, compare-and-delete refuses a mismatched token, remove of a missing row returns
false), run by the in-memory, SQL Server and Cosmos test projects. Registration tests assert the
new interface resolves to the aggregate.

The acknowledgement store is not part of `IMessageTrackingStore`, so the OpenTelemetry decorator
(which wraps only `IMessageTrackingStore`) does not change.

### Policy — `MonitorAcknowledgementService` (WebApp)

Owns the 4 h TTL (`TimeProvider`-driven) and recovery rule so every client sees the same state:

- **Acknowledge(endpointId, reason, user)**: canonical endpoint id from the platform, reason
  trimmed and capped at 500 chars, `FailedCountAtAcknowledgement` = *fresh* (uncached)
  `FailedCount + DeadletterCount`, `ExpiresAtUtc = now + 4h`, new `AcknowledgementId`, upsert.
- **List()**: reads all ACKs, then lazily removes (compare-and-delete) any that are
  - expired (`now >= ExpiresAtUtc`), or
  - recovered: `FailedCountAtAcknowledgement > 0`, the endpoint's current failed total is 0,
    and that count was taken *after* the ACK (`EndpointStateCount.EventTime > AcknowledgedAtUtc`
    — the count comes from the same 5 s `IStoreResultCache` entry the status API uses, so a
    cached pre-ACK snapshot cannot clear a fresh ACK).
  Returns the survivors.
- **Clear(endpointId)**: unconditional remove.

Because the client polls every 5 s, lazy clean-up on read matches today's behavior (the browser
also only cleared on the poll that observed recovery) while making it shared.

### API (`Monitor` tag)

| Operation | Auth | Result |
| --- | --- | --- |
| `GET /api/monitor/acknowledgements` | Reader; filtered to endpoints the caller can read that exist on the platform | `MonitorAcknowledgement[]` |
| `PUT /api/monitor/acknowledgements/{endpointId}` body `{ reason }` | Contributor on the endpoint (site Contributor+ implied) — same bar as resubmit/skip | `MonitorAcknowledgement`; 400 reason too long, 403, 404 unknown endpoint |
| `DELETE /api/monitor/acknowledgements/{endpointId}` | Contributor on the endpoint | 204; 403; 404 unknown endpoint |

`MonitorAcknowledgement`: `endpointId`, `acknowledgementId`, `reason`, `acknowledgedBy`,
`acknowledgedAt`, `expiresAt`, `failedCountAtAcknowledgement`.

### Client

- Every poll fetches status counts and ACKs together; ACKs come from the server, not
  `localStorage`. The legacy `nb.monitor.acks.v1` key is removed once on load.
- The ticker diffs consecutive ACK sets, so an ACK placed or cleared on another device shows up
  ("acked · reason · by alice", "auto-cleared · recovered", "auto-cleared · 4h expiry",
  "ack cleared"). Local actions update the known set immediately so they are not reported twice.
- ACK/un-ACK are optimistic; a failed request reverts and surfaces the error.
- The ACK button is enabled only for callers with Contributor on the endpoint (site role or
  endpoint grant, compared case-insensitively like `endpoint-row-actions`); readers — typically
  the wall PC's account — still see "✓ acked" and who acked it.

## Compatibility

- `IEndpointAcknowledgementStore` joins `INimBusMessageStore`: third-party implementations of the
  aggregate must add three members. This lands with v4.0.0 (untagged major); record it in the v4
  breaking-change ledger.
- `EndpointStateCount.OldestFailureAt` and `EndpointStatusCount.oldestFailureAt` are additive.
- SQL migration `0020` is additive; Cosmos creates the new container on first use.
- Existing browser-local ACKs are dropped on upgrade (they were per-device and expire within 4 h).

## Verification

- `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`.
- Live SQL Server and Cosmos conformance (`NIMBUS_SQL_TEST_CONNECTION`,
  `NIMBUS_COSMOS_TEST_CONNECTION` + `NIMBUS_COSMOS_TEST_GATEWAY=1`) against local containers;
  report any skips.
- WebApp tests: service policy (TTL, recovery, stale-snapshot guard, compare-and-delete),
  API authorization (Reader filter, Contributor for PUT/DELETE, 404), Mapper.
- ClientApp: `npm run test:ci`, `npm run lint`, `npm run build`; hook tests for server-seeded
  `firstFailureAt`, server ACKs, cross-device ticker entries and failed-mutation revert.
- Screenshot of the Monitor with a shared ACK and "Failing for" for the PR.
