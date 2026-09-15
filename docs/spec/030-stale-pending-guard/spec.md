# Spec 030 — Stale message copies must not reopen a settled audit row

Status: **implemented** (2026-09-15, branch `feat/stale-write-guard`; plan:
`docs/plan/2026-09-15-stale-pending-guard-plan.md`). §9 (repairing the corrupted production rows)
and the `nb container reconcile-stale-pending` verb are deliberately out of that change and still
open. DIS's history-only handoff settlement projection was evaluated for this spec on 2026-09-15
and not adopted (Spec 031 §3.6); the rule below keeps the settlement requests in its
control-request set.
Trigger: production incident on EET `Nav09Endpoint`, 2026-09-14, NimBus 3.5.1, Cosmos DB store
Baseline: master `84e63e6` (v3.6.1). Every file named below is byte-identical to the deployed v3.5.1.
Review status: design reviewed adversarially (three independent passes); §13 lists what changed.
Companion: Spec 031 (Resolver capacity controls and Cosmos capacity visibility, ported from DIS)
reduces how often the Resolver is throttled in the first place; this spec makes the resulting
reorder harmless. Ship both.

## 1. Incident

About 100 `AccountUpdated` events on `Nav09Endpoint` show `ResolutionStatus = Pending` although the
NAV adapter completed them. The Flow tab of event `0b89d545` (timestamps are `EnqueuedTimeUtc` on
the Resolver topic, UTC):

| Message on the Resolver topic | Enqueued | Resolver wrote |
|---|---|---|
| `ResolutionResponse` Nav09Endpoint -> Resolver (`906c2b00…`) | 23:49:02.631 | Completed |
| `EventRequest` CrmEndpoint -> Nav09Endpoint (`9ada4185…`) | 23:51:38.679 | Pending at 23:53:19.011 |

The Resolver processed the terminal response first and the request copy second. The request's
Pending write replaced the Completed row.

## 2. Root cause

Three facts combine.

1. **Two copies of every event reach the Resolver, and nothing orders them by causality.** The
   publisher sends the original to the CrmEndpoint topic with `To = <eventTypeId>`. The
   `Nav09Endpoint` forwarder subscription on that topic rewrites it (`SET user.From = 'CrmEndpoint';
   SET user.EventId = newid(); SET user.To = 'Nav09Endpoint'`, `TopologyDescriptor.cs:110`) and
   forwards it into the Nav09Endpoint topic. There, the consumer subscription delivers it to the NAV
   adapter, and the session-less `Resolver` fan-out subscription (rules `to-Nav09Endpoint` and
   `from-Nav09Endpoint`, `TopologyDescriptor.ResolverFanoutSubscription`) auto-forwards a copy to the
   Resolver topic. The subscriber's `ResolutionResponse` is sent on the same Nav09Endpoint topic and
   forwarded by the same fan-out. The Resolver subscription is session-enabled, so it is FIFO per
   session **by enqueue order on the Resolver topic**. Auto-forwarding is a broker-side background
   job with no documented ordering guarantee, so a request copy can in principle be overtaken by the
   response, but both copies share the final hop and the request enters it first, so a 156 s
   inversion inside one forwarding pipe is implausible. Verify the deployed rule set against the
   descriptor on the live namespace (WebApp Admin -> Subscriptions).
2. **`ScheduleRedelivery` reorders for real, for every message type, and it is what happened here.**
   On Cosmos throttling or a transient storage failure, `ResolverService` calls
   `MessageContext.ScheduleRedelivery` (`src/NimBus.ServiceBus/MessageContext.cs:717`), which
   completes the original and re-sends a scheduled copy with a **new Service Bus `MessageId`**
   (`Guid.NewGuid().ToString()`, a dashed GUID), the same `SessionId`, body and application
   properties. The copy lands behind any message already queued in the session. Two facts tie the
   incident to this path: the 156 s gap on `0b89d545` equals five backoff rounds (5+10+20+40+80 s),
   and the late copy's id `9ada4185-3bcd-…` is a dashed GUID, whereas the SDK publisher stamps
   `MessageId = "{eventType}-{hash}"` (`PublisherClient.GetMessageStatic`) and auto-forwarding
   preserves it. The Flow tab shows no deferral, which excludes the deferred-drain republish. The
   same path also delays a throttled `DeferralResponse` or `PendingHandoffResponse`, whose late copy
   leaves a row stuck as Deferred or Pending+Handoff with nothing behind it to correct it.
3. **Every status write is last-writer-wins.** All three providers write the row
   `{eventId}_{sessionId}` unconditionally: `CosmosDbMessageTrackingStore.UploadMessage`
   (`UpsertItemAsync`, no ETag, no status check; it also resets `deleted = false` and the unresolved
   TTL, which is why Completed rows resurface), `SqlServerMessageTrackingStore.UpsertStatus`
   (`MERGE … WHEN MATCHED THEN UPDATE`), `InMemoryMessageStore.Upsert` (indexer assignment).
   `ResolverService.MessageTypeToStatusMap` maps every request type to Pending and `UpdateState`
   writes it regardless of what the row holds.

`StoreMessage` (per-message history) runs before `UpdateState`, which is why the Flow tab still shows
both messages and made the incident diagnosable. Nothing in this design changes that.

Serialization is not the problem: every copy of one event carries the same `SessionId`, the row key
includes the session id, and the Resolver trigger is session-enabled with one call per session.
Writes to one row are serial while the session lock holds. The bug is arrival order, and
timestamps cannot fix it: both `EnqueuedTimeUtc` and the sequence number are re-assigned on the
Resolver topic, and in the incident the stale copy is the *newer* message.

## 3. Goals

1. A message copy that arrives after the outcome it belongs to must never change the audit row.
2. Cover both mechanisms (fan-out lag, `ScheduleRedelivery`) and all three write classes they can
   delay: request copies (`EventRequest`), deferrals (`DeferralResponse`) and handoff parks
   (`PendingHandoffResponse`).
3. Provider parity: Cosmos, SQL Server and the in-memory store behave identically, pinned by the
   conformance suite in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`.
4. Every legitimate transition away from a terminal status keeps working (§6).
5. No interface signature, wire, schema, topology or `host.json` change.

## 4. Non-goals

- Making auto-forwarding order-preserving; it is broker-side and out of reach of user code.
- Terminal-over-terminal reorders (for example a throttled `ErrorResponse` landing after a retry's
  `ResolutionResponse`). They stay last-writer-wins; a per-row state machine is out of scope.
- Replacing `ScheduleRedelivery` with lock-holding waits. Possible follow-up (§11).

## 5. Design

### 5.1 The rule

One static class, `NimBus.MessageStore.StaleWriteGuard` in `NimBus.MessageStore.Abstractions`, is
the single source of truth. Every provider applies it *inside* its non-terminal status write,
atomically against the row it is about to replace. The decision uses only fields every provider
already persists on the row: `ResolutionStatus`, `MessageType` (what last wrote the row) and
`ParentMessageId`.

Vocabulary:

- Terminal statuses: Completed, Skipped, Failed, DeadLettered, Unsupported.
- Request-stage writes: `EventRequest` (projected as Pending) and `DeferralResponse` (Deferred).
  A row last written by one of these is a *request-stage row*.
- Control requests: `ResubmissionRequest`, `SkipRequest`, `RetryRequest`, `ContinuationRequest`,
  `HandoffCompletedRequest`, `HandoffFailedRequest` (all projected as Pending). They are operator or
  manager intent. (DIS's history-only projection of the two settlement requests was evaluated on
  2026-09-15 and not adopted; see Spec 031 §3.6 and the ADR-012 note.)
- Handoff park: `PendingHandoffResponse` (Pending with `PendingSubStatus = "Handoff"`).
- Everything else (`MessageType.Unknown` from CLI and test seeds) is not guarded.

Decision for an incoming write with status `S`, content `c`, against the existing row `r`:

1. `r` absent, or `S` terminal, or `c.MessageType` not guarded: **apply** (unchanged behaviour).
2. Ancestor check: if `r.ParentMessageId` is non-empty and equals `c.LastMessageId`, **refuse**.
   `ResponseService.CreateResponse` stamps every response's `ParentMessageId` with the message it
   answers, and `HandoffControlMessageFactory` stamps a settlement's `ParentMessageId` with the
   handoff response it settles. A row whose last write names this message as its parent therefore
   already holds this message's outcome. Original requests arrive with `ParentMessageId = "self"`
   (`MessageContext.cs:115`), so first writes are unaffected.
3. By incoming type:
   - request-stage write: **apply only if `r` is a request-stage row that is not terminal**, that is
     `r.ResolutionStatus` is Pending or Deferred and `r.MessageType` is `EventRequest` or
     `DeferralResponse` (lenient: `Unknown` rows count as request-stage too). A request copy may
     refresh its own projection or a deferral of itself; it may never replace a row written by a
     control request, a handoff park or a terminal response.
   - handoff park: **refuse only if `r.ResolutionStatus` is Completed or Skipped**. Failed must stay
     open because a policy retry (`RetryRequest`, never seen by the Resolver) parks a handoff straight
     from a Failed row (`StrictMessageHandler.HandleRetryRequest`); Failed, DeadLettered and
     Unsupported must all stay open because a resubmission's park can overtake the resubmission's
     own throttled Pending write.
   - control request: **apply** (operator intent stays unconditional; the ancestor check above is
     the only limit).

```csharp
namespace NimBus.MessageStore;

/// <summary>
/// Store-side rule that stops a late copy (auto-forward lag, ScheduleRedelivery copy, DLQ replay,
/// double deferred drain) from reopening or downgrading an audit row. Applied inside every
/// provider's non-terminal status write; pinned by the conformance suite.
/// </summary>
public static class StaleWriteGuard
{
    public static bool IsTerminal(ResolutionStatus s) =>
        s is ResolutionStatus.Completed or ResolutionStatus.Skipped or ResolutionStatus.Failed
          or ResolutionStatus.DeadLettered or ResolutionStatus.Unsupported;

    public static bool IsRequestStage(MessageType t) =>
        t is MessageType.EventRequest or MessageType.DeferralResponse or MessageType.Unknown;

    public static bool IsControlRequest(MessageType t) =>
        t is MessageType.ResubmissionRequest or MessageType.SkipRequest or MessageType.RetryRequest
          or MessageType.ContinuationRequest or MessageType.HandoffCompletedRequest or MessageType.HandoffFailedRequest;

    /// <summary>True when the incoming write is subject to the rule (and so needs the current row).</summary>
    public static bool IsGuarded(ResolutionStatus incomingStatus, MessageType incomingType) =>
        !IsTerminal(incomingStatus)
        && (incomingType is MessageType.EventRequest or MessageType.DeferralResponse or MessageType.PendingHandoffResponse
            || IsControlRequest(incomingType));

    /// <summary>True when the incoming write may replace <paramref name="current"/> (null = absent).</summary>
    public static bool Allows(ResolutionStatus incomingStatus, UnresolvedEvent incoming, UnresolvedEvent? current)
    {
        if (current is null || !IsGuarded(incomingStatus, incoming.MessageType)) return true;

        if (!string.IsNullOrEmpty(current.ParentMessageId)
            && string.Equals(current.ParentMessageId, incoming.LastMessageId, StringComparison.Ordinal))
            return false; // the row already holds this message's outcome

        return incoming.MessageType switch
        {
            MessageType.EventRequest or MessageType.DeferralResponse =>
                !IsTerminal(current.ResolutionStatus) && IsRequestStage(current.MessageType),
            MessageType.PendingHandoffResponse =>
                current.ResolutionStatus is not (ResolutionStatus.Completed or ResolutionStatus.Skipped),
            _ => true, // control requests
        };
    }
}
```

The `deleted` flag is deliberately not an input: Cosmos soft-deletes Completed rows
(`deleted = true`, 30-day TTL) while SQL keeps them at `Deleted = 0`, and archived Failed rows must
stay reopenable by `ResubmissionRequest`.

### 5.2 Return-value contract

`IMessageTrackingStore.Upload*Message` already returns `Task<bool>`; today Cosmos and in-memory
always return `true`, SQL returns `rows > 0`, and the Resolver discards the value. The contract
becomes: `true` = row created or replaced; `false` = refused by `StaleWriteGuard`, nothing written.
Provider failures, including a lost compare-and-swap race, must throw, never return `false`.
Document this on the interface members. No signature changes, so the OpenTelemetry decorator, test
fakes and external providers keep compiling.

### 5.3 Cosmos DB (`CosmosDbMessageTrackingStore`)

`UploadPendingMessage` and `UploadDeferredMessage` route guarded content
(`StaleWriteGuard.IsGuarded(status, content.MessageType)`) to one private method; everything else
keeps using `UploadMessage` unchanged. The guarded path is the point-read + `IfMatchEtag` replace
pattern already used by `CosmosDbServiceHealthStore.TryClaimServiceProbe`, on existing
`ICosmosContainerAdapter` members (`ReadItemAsync`, `CreateItemAsync`, `UpsertItemAsync` with
`ItemRequestOptions`). A 429 on any call is translated to `RequestLimitException` by the
transient-translating adapter and takes the existing throttle path. 404, 409 and 412 are not
transient (`CosmosExceptionTranslation.IsTransient`) and are handled here, because a raw
`CosmosException` would dead-letter the message in `ResolverService`.

```csharp
private async Task<bool> UploadGuarded(string eventId, string sessionId, string endpointId, UnresolvedEvent content, string status)
{
    var container = await _getEndpointContainer(endpointId);
    var eventDbo = new EventDbo
    {
        Id = $"{eventId}_{sessionId}", Event = content, SessionId = sessionId, Status = status,
        EventType = content.EventTypeId, Deleted = false, TimeToLive = _unresolvedTtlSeconds,
    };
    var pk = new PartitionKey(eventDbo.Id);
    var incomingStatus = Enum.Parse<ResolutionStatus>(status);

    for (var attempt = 0; attempt < 3; attempt++)
    {
        ItemResponse<EventDbo> current;
        try
        {
            current = await container.ReadItemAsync<EventDbo>(eventDbo.Id, pk); // point read; sees deleted=true docs
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                await container.CreateItemAsync(eventDbo, pk); // first projection (no options overload on the adapter today)
                return true;
            }
            catch (CosmosException e2) when (e2.StatusCode == HttpStatusCode.Conflict) { continue; } // created concurrently: re-read
        }

        var row = HydrateResolutionStatus(current.Resource); // UnresolvedEvent with ResolutionStatus set from the document's status
        if (row is null || !Enum.TryParse<ResolutionStatus>(current.Resource.Status, out _))
        {
            // Fail closed: never treat an unreadable row as in-flight.
            _logger?.LogWarning("COSMOS UPSERT-REFUSED: row {Id} has unparseable status {Status}", eventDbo.Id, current.Resource.Status);
            return false;
        }

        if (!StaleWriteGuard.Allows(incomingStatus, content, row))
        {
            _logger?.LogInformation(
                "COSMOS UPSERT-REFUSED: stale {MessageType} {MessageId}; row {Id} already {Status} written by {RowMessageType} {RowMessageId}",
                content.MessageType, content.LastMessageId, eventDbo.Id, current.Resource.Status, row.MessageType, row.LastMessageId);
            return false;
        }

        try
        {
            await container.UpsertItemAsync(eventDbo, pk,
                new ItemRequestOptions { IfMatchEtag = current.ETag, EnableContentResponseOnWrite = false });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Row changed between read and replace (WebApp patch, lock-loss twin): re-read and re-evaluate.
        }
    }

    // A lost race is a transient failure, not a refusal: the Resolver reschedules and re-evaluates idempotently.
    throw new StorageProviderTransientException($"Audit row {eventDbo.Id} changed concurrently three times.");
}
```

The adapter's `CreateItemAsync` has no `ItemRequestOptions` overload, so the first write of each
event echoes the document body back once (bandwidth only, RU unchanged). Accepted for this change;
adding the overload (same default-implementation pattern as `DeleteItemAsync` with options) is an
optional follow-up (§11).

Cost: one point read (about 1 RU) per guarded write, that is once per event for the request
projection plus operator-rate control requests. Terminal writes pay nothing extra. A refused write
saves the full-document upsert the stale copy used to perform.

### 5.4 SQL Server (`SqlServerMessageTrackingStore.UpsertStatus`)

Transliterate the rule into the existing MERGE and make the write serializable per key with
`WITH (HOLDLOCK)`, the hint the same file already uses on the `EventReports` MERGE for the same
reason. That also closes the pre-existing first-insert race (unique violation 2627 -> generic catch
-> dead-letter). `@Status`, `@MessageType` and `@LastMessageId` are already bound; `Status`,
`MessageType`, `ParentMessageId` (`0003_Events.sql`) already exist. No DbUp migration, because the
MERGE text lives in C#. Return the outcome explicitly instead of relying on Dapper's records-affected
count (which `SET NOCOUNT ON` turns into -1).

```sql
MERGE {T("UnresolvedEvents")} WITH (HOLDLOCK) AS target
USING (SELECT @EventId AS EventId, @SessionId AS SessionId, @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId AND target.EventId = source.EventId
   AND ((target.SessionId IS NULL AND source.SessionId IS NULL) OR target.SessionId = source.SessionId)
-- StaleWriteGuard, transliterated (keep in step with StaleWriteGuard.Allows):
WHEN MATCHED AND (
        @Status IN ('Completed','Skipped','Failed','DeadLettered','Unsupported')                        -- terminal writes
     OR @MessageType NOT IN ('EventRequest','DeferralResponse','PendingHandoffResponse',
                             'ResubmissionRequest','SkipRequest','RetryRequest','ContinuationRequest',
                             'HandoffCompletedRequest','HandoffFailedRequest')                          -- not guarded
     OR (
            (NULLIF(target.ParentMessageId, '') IS NULL OR NULLIF(@LastMessageId, '') IS NULL
             OR target.ParentMessageId COLLATE Latin1_General_BIN2 <> @LastMessageId COLLATE Latin1_General_BIN2)  -- ancestor check
        AND CASE
              WHEN @MessageType IN ('EventRequest','DeferralResponse') THEN
                   CASE WHEN target.Status IN ('Pending','Deferred')
                         AND (target.MessageType IS NULL OR target.MessageType IN ('EventRequest','DeferralResponse','Unknown')) THEN 1 ELSE 0 END
              WHEN @MessageType = 'PendingHandoffResponse' THEN
                   CASE WHEN target.Status NOT IN ('Completed','Skipped') THEN 1 ELSE 0 END
              ELSE 1                                                                                     -- control requests
            END = 1
        )
) THEN UPDATE SET
    -- all existing columns unchanged, including Deleted = 0
WHEN NOT MATCHED THEN INSERT ( -- unchanged
;
SELECT @@ROWCOUNT;
```

Execute with `QuerySingleAsync<int>` and return `> 0`.

### 5.5 In-memory (`InMemoryMessageStore.Upsert`)

Decide against the stored entry before stamping the caller's object, and report the factory's own
decision rather than a reference comparison, so passing the same instance twice cannot bypass the
rule:

```csharp
private Task<bool> Upsert(string eventId, string sessionId, string endpointId, ResolutionStatus status, UnresolvedEvent content)
{
    var applied = false;
    UnresolvedEvent Stamp()
    {
        content.ResolutionStatus = status; content.UpdatedAt = DateTime.UtcNow;
        content.EndpointId = endpointId; content.EventId = eventId; content.SessionId = sessionId;
        applied = true;
        return content;
    }
    _events.AddOrUpdate(Key(endpointId, eventId, sessionId),
        _ => Stamp(),
        (_, existing) => StaleWriteGuard.Allows(status, content, existing) ? Stamp() : existing);
    return Task.FromResult(applied);
}
```

Known, pre-existing parity difference left alone: `ArchiveFailedEvent` hard-removes the row in-memory
but soft-deletes in Cosmos and SQL. A stale request copy after an archive is therefore applied
in-memory (row absent, a visible Pending row appears) and refused in Cosmos/SQL (Failed row still
present). No conformance case covers that sequence because the outcomes legitimately differ;
Resolver unit tests must not depend on the hard delete. A soft-delete flag for the in-memory store
is a follow-up (§11).

### 5.6 Resolver (`ResolverService`)

`UpdateState` returns `(ResolutionStatus Status, bool Applied)`; the handler dictionary becomes
`Func<Task<bool>>` with unchanged bodies; `InstrumentOutcomeWrite` returns the bool and tags the
`RecordOutcome` activity with `nimbus.outcome.applied`. In `Handle`:

- applied: the existing "Resolver: Updated Endpoint … Status:{Status}" line and
  `NotifyEndpointStateChangedAsync`, as today.
- refused: one warning, no notification, and a `MessageAuditType.Comment` audit through the existing
  `InstrumentAuditWrite` plumbing (`AuditorName = "Resolver"`, `Data = "Ignored stale <MessageType>
  <MessageId>: row already <status>"`). The audit shows in the WebApp audit listing with no UI
  change and gives SQL deployments an attribution the store itself cannot log.

```csharp
_logger?.LogWarning(
    "Resolver: Ignored stale {MessageType}; audit row already reflects a later state. EndpointId:{EndpointId}, EventId:{EventId}, SessionId:{SessionId}, MessageId:{MessageId}, EnqueuedTimeUtc:{EnqueuedTimeUtc}, ThrottleRetryCount:{ThrottleRetryCount}, DeliveryCount:{DeliveryCount}",
    messageEntity.MessageType, messageEntity.EndpointId, messageEntity.EventId, messageEntity.SessionId,
    messageContext.MessageId, messageEntity.EnqueuedTimeUtc, messageContext.ThrottleRetryCount, deliveryCount);
```

`ThrottleRetryCount > 0` attributes the copy to `ScheduleRedelivery`; `ThrottleRetryCount == 0` with
`DeliveryCount == 1` to fan-out lag or DLQ replay; `DeliveryCount > 1` to broker redelivery. The
message is completed either way: its history document is already stored, so the Flow tab keeps
showing the late copy after the response, the forensic trail that found this incident.

Metrics: a counter `nimbus.resolver.outcome_ignored` next to `ResolverOutcomeWritten` in
`NimBusMeters`, incremented instead of `outcome_written` on a refusal. Release note: the
"Updated Endpoint" log line and `outcome_written` are no longer one-per-message; dashboards that
used them as throughput should sum `outcome_written + outcome_ignored`.

### 5.7 `ScheduleRedelivery` keeps the original `MessageId`

Change `MessageContext.ScheduleRedelivery` (`MessageContext.cs:725`) from
`MessageId = Guid.NewGuid().ToString()` to `MessageId = receivedMessage.MessageId`. With the original
id preserved, the ancestor check recognises **every** rescheduled copy of every guarded type, which
closes the one hole the status rules cannot: a rescheduled `ResubmissionRequest`, `SkipRequest` or
handoff settlement arriving after its own response, which the subscriber never sees again and which
would otherwise reopen the row permanently. Bulk resubmit after a throttling incident is exactly
the load that produces such copies.

Why it is safe: `ScheduleRedelivery` has one caller (`ResolverService`); the history write is
idempotent for a repeated id in all three providers (Cosmos upserts by `id = MessageId`, SQL guards
the insert with `IF NOT EXISTS (EventId, MessageId)`, in-memory sets a dictionary key), so a
throttle chain now leaves one history document instead of one per round; the delivery budget in
`HandleCosmosThrottle` uses `ThrottleRetryCount + DeliveryCount` and is unaffected.

**Precondition:** the Resolver topic must not have `RequiresDuplicateDetection` enabled, otherwise
the broker silently drops the same-id copy inside the detection window and the audit update is
lost. The provisioner never enables it (`ServiceBusTopologyProvisioner.cs` sets only the window
value), but the EET namespace is brownfield: verify the live topic before deploying. If it is
enabled, use the alternative instead: stamp a `RedeliveryOfMessageId` application property in
`ScheduleRedelivery`, expose it on `MessageEntity`/`UnresolvedEvent`, and extend the ancestor check
to also compare it.

Apply the same one-line change to the WebApp's Resolver DLQ replay
(`ResolverDeadLetterClient.CloneForReplay`, which today overrides the id with a new GUID and already
records `DeadLetterOriginalMessageId`), so a replayed control request cannot reopen a settled row
either. Two existing tests assert the old new-id behaviour and must be flipped:
`MessageContextTests.ScheduleRedelivery_CopiesBodyAndStandardProperties_WithNewMessageId` and
`ResolverDeadLetterClientTests.CloneForReplay_PreservesSendableMetadataAndReplacesDeadLetterFields`.
Update the acceptance item in `docs/plan/resolver-dead-letter-replay.md` that promises a fresh id.

## 6. Legitimate transitions and why each still works

| Flow | Message the Resolver sees | Row before | Guard outcome |
|---|---|---|---|
| Happy path, first write | `EventRequest` | absent | Created. |
| Broker redelivery of the same request after lock loss | same `EventRequest`, `DeliveryCount > 1` | Pending written by that request | Request-stage row. Applied (idempotent re-stamp). |
| Deferred drain (`DeferredMessageProcessor`) | republished `EventRequest`, same `EventId`/`SessionId`, `ParentMessageId` copied from the parked copy | Deferred | Request-stage row. Applied (Deferred -> Pending). A second copy after a crash lands on a terminal row and is refused. |
| Re-deferral during a drain | `DeferralResponse` | Deferred or Pending (request) | Request-stage row. Applied. |
| Operator Resubmit (`ManagerClient.Resubmit`) | `ResubmissionRequest`, fresh `MessageId`, `ParentMessageId = errorResponse.MessageId` | Failed / DeadLettered / Unsupported (possibly archived); Completed via direct API | Control request; row parent is the original request id, never the fresh id. Applied, `Deleted = 0` / `deleted = false` restored as today. |
| Operator Skip (`ManagerClient.Skip`) | `SkipRequest`, fresh id | Failed / DeadLettered / Unsupported | Same. The following `SkipResponse` is terminal, unguarded. |
| Handoff park | `PendingHandoffResponse` | Pending (request or control row), Deferred, or Failed (policy retry) | Allowed statuses. Applied. |
| Handoff settlement (SDK `HandoffClient` or WebApp `HandoffSettlementService`) | `HandoffCompletedRequest` / `HandoffFailedRequest`, `MessageId = Guid.NewGuid()`, `ParentMessageId` = the handoff response's id | Pending+Handoff | Control request; fresh Guid never equals the row's parent. Applied as plain Pending with `PendingSubStatus` cleared, which is what the agent zone's receive loop and settle guard rely on (ADR-012, 2026-09-15 note). |
| Subscriber inbox duplicate | `SkipResponse` (DuplicateDetected) | any | Terminal write, unguarded. Unchanged. |
| Admin bulk skip, CLI `nb container resubmit/skip`, WebApp archive/remove/purge patches, test seeds | Skipped / Failed writes, `PatchItemAsync`, or `MessageType.Unknown` | any | Terminal, patch, or not guarded. Unchanged. The CLI stamps `MessageType = EventRequest` on a **Failed** write (`Container.cs`); that row is terminal, so a late request copy is refused. |
| `RetryRequest` / `ContinuationRequest` | addressed to `Retry` / `Continuation` | any | The provisioned fan-out rules never match them, so the Resolver does not see them; a legacy namespace that forwards them applies them as control requests. |

What the rule newly protects:

- Completed, Skipped, Failed, DeadLettered and Unsupported rows against late `EventRequest` and
  `DeferralResponse` copies (the incident, and the stuck-Deferred variant).
- Completed and Skipped rows against a late `PendingHandoffResponse` copy (phantom "awaiting
  external" rows that `GetNextPendingHandoffEvent` would hand to agents).
- Pending rows written by a control request or a handoff park against late request copies, so
  `HandoffSettlementService`'s gate on `PendingSubStatus == "Handoff"` and
  `GetPendingHandoffByExternalJobId` keep working, and a resubmission's projection is not overwritten
  by the original request's late copy.
- With §5.7, every rescheduled copy of every guarded type.

Existing conformance tests were checked for terminal-then-non-terminal sequences on one key:
`Status_transition_replaces_previous_status` (Pending then Completed) and
`UploadStatus_is_idempotent_under_repeated_writes` (Failed twice) are unguarded writes;
`GetEndpointErrorList_returns_failed_and_deferred_event_ids` and
`GetNextPendingHandoffEvent_returns_only_the_handoff_row` use distinct event ids. `SampleEvent` has
`MessageType = EventRequest`, `LastMessageId = "last-message"` and no `ParentMessageId`, so the
ancestor clause never trips on it. The Cosmos retention and write-option tests and the WebApp agent
and handoff tests seed with `MessageType.Unknown`, which the rule treats as request-stage.

## 7. Tests

Conformance (`MessageTrackingStoreConformanceTests`; runs on in-memory, the Cosmos emulator and the
SQL container). Follow the file's conventions: `[TestMethod]` with `[DataRow]` where useful, and
loop statuses inside **one** endpoint container with distinct event ids (as
`All_lookup_resolution_statuses_round_trip` does), because every distinct endpoint id is a new
Cosmos container on the emulator. Pass a fresh `SampleEvent` per call.

1. `Stale_request_copy_does_not_regress_settled_rows`: for each terminal status, Pending(EventRequest)
   -> terminal -> Pending(EventRequest, fresh `LastMessageId`) returns `false`; `PendingCount == 0`;
   `GetEvent` still reports the terminal status. Same loop for `DeferralResponse`
   (`UploadDeferredMessage`): returns `false`, `DeferredCount == 0`.
2. `Request_copy_refreshes_request_stage_rows`: Pending(EventRequest, m1) -> Pending(EventRequest, m2)
   both `true`, `GetPendingEvent().LastMessageId == m2` (this exercises the Cosmos ETag replace);
   Deferred -> Pending(EventRequest) `true`; Pending(EventRequest) -> Deferred `true`.
3. `Request_copy_does_not_replace_control_or_handoff_rows`: Pending(ResubmissionRequest) ->
   Pending(EventRequest) `false` and `GetPendingEvent().MessageType == ResubmissionRequest`;
   Pending(PendingHandoffResponse, `PendingSubStatus = "Handoff"`, `ExternalJobId = "job-1"`) ->
   Pending(EventRequest) `false` and `GetPendingHandoffByExternalJobId` still returns the row;
   Pending(HandoffCompletedRequest) -> Pending(EventRequest) `false`.
4. `Handoff_park_is_refused_over_completed_and_skipped_only`:
   Completed -> Pending(PendingHandoffResponse) `false`; Skipped -> `false`; Failed, DeadLettered,
   Unsupported, Deferred and Pending(ResubmissionRequest) -> `true`, each with
   `GetPendingEvent().PendingSubStatus == "Handoff"`.
5. `Handoff_settlement_clears_substatus`: Pending(PendingHandoffResponse, "Handoff", "job-1") ->
   Pending(HandoffCompletedRequest, fresh id) `true`, `GetPendingHandoffByExternalJobId` returns null.
   (The Resolver side of this projection is already pinned by
   `Handle_HandoffSettlementRequest_ProjectsPlainPendingRow`.)
6. `Control_request_reopens_settled_rows` (`[DataRow]`): (Failed, ResubmissionRequest),
   (DeadLettered, SkipRequest), (Completed, ResubmissionRequest), (Unsupported, ResubmissionRequest)
   -> `true`, `PendingCount == 1`.
7. `Write_answered_by_row_is_refused` (ancestor check): Pending(ResubmissionRequest, rs-1) ->
   Completed(ResolutionResponse, `ParentMessageId = rs-1`) -> Pending(ResubmissionRequest, rs-1)
   `false`; Pending(ResubmissionRequest, rs-2) `true`.
8. `Pending_write_after_ArchiveFailedEvent_revives_row`: Failed -> `ArchiveFailedEvent` ->
   Pending(ResubmissionRequest) `true`, `GetPendingEvent` returns the row. No case for "archive then
   stale request copy": the outcome legitimately differs per provider (§5.5).
9. A plain first write of every status returns `true` (guards the SQL `@@ROWCOUNT` path).

Resolver unit tests (`tests/NimBus.Resolver.Tests`). First hoist `CreateMessageContext` and
`FakeMessageContext` out of `ResolverServiceTests` into an internal shared helper (they are
`private static` and nested today), and let the helper take `messageId` and `parentMessageId`.

- `FakeCosmosDbClient` returning `false` from `UploadPendingMessage`: `Handle` completes the message,
  does not dead-letter or reschedule, stores the history entry, writes the Comment audit, and does not
  notify. Control: an applied write still notifies.
- Over the real `InMemoryMessageStore` (add a project reference to `NimBus.Testing`; no cycle):
  incident order (`ResolutionResponse` with `ParentMessageId = request-1`, then a late `EventRequest`
  `request-1` with `ThrottleRetryCount = 5`) leaves the row Completed, `PendingCount == 0`, two
  history entries, both messages completed; fan-out-lag order (request, response, same-id request
  copy) likewise; a late request over Pending+Handoff keeps the handoff; a late request over a
  Pending row written by `HandoffCompletedRequest` is refused; a `ResubmissionRequest` over Failed
  reopens; a late `DeferralResponse` or `PendingHandoffResponse` after Completed keeps Completed; a
  same-id `ResubmissionRequest` arriving after its own response is refused.

Cosmos unit tests with the recording adapter (make `ReadItemAsync` scriptable and let
`CreateItemAsync` record instead of throw): refused path performs no upsert and returns `false`;
request-stage path upserts with `IfMatchEtag` equal to the read ETag; 404 path calls
`CreateItemAsync` with content response suppressed; a 412 triggers a re-read; three 412s throw
`StorageProviderTransientException`; an unparseable status returns `false`.

`ServiceBus` and WebApp unit tests: flip the two existing assertions named in §5.7 so
`ScheduleRedelivery` and `CloneForReplay` are asserted to keep the original `MessageId`; the
`ThrottleRetryCount` replacement test is unchanged.

Commands: `dotnet build src/NimBus.sln -c Release` (CS warnings are errors in Release; CS8767 is not
allowlisted), `dotnet test tests/NimBus.Resolver.Tests`, `dotnet test tests/NimBus.MessageStore.InMemory.Tests`,
`dotnet test tests/NimBus.ServiceBus.Tests`, and the Cosmos and SQL conformance runs with their
connection-string environment variables. CI already has a must-not-skip gate for the Cosmos
conformance run; add the same gate for the SQL run so the MERGE predicate cannot pass by being
inconclusive.

## 8. Rollout

- No migration, no topology change, no `host.json` or app-setting change, no adapter or SDK redeploy.
- Verify the duplicate-detection precondition on the live Resolver topic (§5.7) before deploying.
- Deploy order: Resolver Function App first (the only production caller of the guarded writes and of
  `ScheduleRedelivery`), then WebApp and CLI on their normal cadence.
- Mixed versions are safe both ways. Old store package with new Resolver: today's behaviour. New
  store package with old Resolver: guard active, the bool is discarded, message still completed.
- Third-party `IMessageTrackingStore` implementations keep compiling but fail the new conformance
  cases until they adopt the rule. Release notes plus a minor version bump (3.7.0) are recommended.
- Write the implementation plan in `docs/plan/` once this design is accepted.

## 9. Repairing the corrupted production rows (Cosmos, `Nav09Endpoint`)

Run only after the new Resolver is live, so an in-flight stale copy cannot re-corrupt a repaired row.
The NAV adapter did complete the work, so Resubmit is wrong (it would re-run the handler and insert
a duplicate change-feed row) and bulk Skip is wrong (it would record Skipped).

1. **Identify.** On the `Nav09Endpoint` container:
   `SELECT c.id, c.event.EventId, c.event.SessionId, c.event.MessageType, c.event.LastMessageId, c.event.EnqueuedTimeUtc, c.event.UpdatedAt FROM c WHERE c.status = 'Pending' AND c.event.MessageType IN ('EventRequest','ResubmissionRequest','SkipRequest','HandoffCompletedRequest','HandoffFailedRequest') AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true)`.
   Control-request rows are included because a rescheduled control copy corrupts a row the same way.
2. **Classify per row** from the messages container (`GetEventHistory(eventId)`): take the latest
   `ResolutionResponse` with `From == 'Nav09Endpoint'` and the row's `SessionId`. Repairable when
   that response's `EnqueuedTimeUtc` precedes the row's `EnqueuedTimeUtc` (the incident signature)
   and no `ResubmissionRequest`, `SkipRequest`, `Handoff*Request` or `PendingHandoffResponse` is
   enqueued after it. Rows whose latest terminal is an `ErrorResponse`, or with no terminal at all,
   are listed for an operator decision, not repaired. Keep the list as the audit CSV.
3. **Apply.** For each repairable row, re-read it by id immediately before writing and skip unless it
   is still Pending with the `LastMessageId` captured in step 1 (or, for an atomic apply, use
   `PatchItemAsync` with `FilterPredicate = "FROM c WHERE c.status = 'Pending' AND c.event.LastMessageId = '<stale id>'"`).
   Rebuild the `UnresolvedEvent` as `ResolverService.CreateUnresolvedEvent` would from the stored
   `ResolutionResponse` (`ResolutionStatus = Completed`, `MessageType = ResolutionResponse`,
   `LastMessageId = response.MessageId`, ids, addresses, content and timings from the response,
   `UpdatedAt = now`) and call `CosmosDbClient.UploadCompletedMessage(eventId, sessionId, "Nav09Endpoint", projection)`,
   which produces exactly the document the Resolver would have left (`status = Completed`,
   `deleted = true`, 30-day TTL). Then `StoreMessageAudit` with a Comment entry so the WebApp audit
   listing shows the repair. History documents are untouched.
4. **Verify.** The identification query returns zero rows, `PendingCount` on the endpoint drops by the
   CSV row count, and event `0b89d545` shows Completed with `LastMessageId 906c2b00…`.

Packaging is an open decision (§12): a throwaway console project referencing
`Akaule.NimBus.MessageStore.CosmosDb` and calling those `CosmosDbClient` methods is the smallest; a
reusable `nb container reconcile-stale-pending <endpoint> [--before] [--dry-run]` verb next to
`nb container skip` is the durable option.

Incident confirmation outside the repo: search Resolver logs for "Cosmos DB throttled. Scheduling
redelivery" with `EventId 0b89d545` around 23:48–23:52 UTC. The MessageId shape already points at
the reschedule path (§2); the logs make it certain and show how many rounds ran. The fix does not
depend on the answer.

## 10. Residual risks

- Cosmos adds one throttle-able point read on the hottest path; under a 429 storm it is handled like
  today's throttles, and the resulting rescheduled copy is now harmless.
- Same-id redelivery changes forensics: a throttle chain leaves one history document instead of one
  per round, so the Flow tab no longer shows a second request entry for a rescheduled copy. The
  Resolver warning, the `outcome_ignored` counter and the Comment audit carry the attribution.
- A late `PendingHandoffResponse` copy over a Failed, DeadLettered or Unsupported row is still
  applied (those statuses must stay open for the policy-retry park and for a resubmission's park
  overtaking its own Pending write). Consequence: a phantom Pending+Handoff row that an operator can
  Skip or Resubmit. Rare: it needs a throttled park and a failure of the same handoff in one window.
- A late request copy over a Deferred row of the same event is applied (request-stage), hiding the
  Deferred state until the drain republishes. Same as today. Tightening it would require comparing
  `ParentMessageId` between the drain republish and the Deferred row; not worth the extra clause.
- Terminal-over-terminal reorders stay last-writer-wins (non-goal).
- A Completed Cosmos document expires after 30 days; a copy replayed later than that creates a
  fresh Pending row exactly as today. Replay tooling should skip copies whose event already has a
  terminal response in history.
- Under session-lock loss with two instances handling the same first request, the loser's 409
  re-read finds a request-stage Pending row and applies (idempotent), or a terminal row and refuses
  with a warning that reads "ignored stale" for a copy that was merely concurrent (cosmetic).
- Pre-existing and unchanged: the WebApp Resubmit race between `ArchiveFailedEvent` and the
  `ResubmissionRequest` Pending write (whichever lands last wins on the deleted flag).

## 11. Follow-ups (not in this change)

- Add a `CreateItemAsync` overload with `ItemRequestOptions` to `ICosmosContainerAdapter` so the
  first write of each event stops echoing the document body.
- (Retired.) "Hold the session lock and abandon instead of re-sending" was evaluated in Spec 031
  §3.5 against the DIS implementation and rejected: the retry cadence would be governed by
  session release rather than an explicit backoff, `RetryAfter` would be ignored, and order is not
  preserved either way. `ScheduleRedelivery` with the original `MessageId` (§5.7) stays.
- Reject direct-API Resubmit of Completed or Skipped rows in `EventImplementation` if that is
  considered operator error rather than intent.
- Give the in-memory store a soft-delete flag for full parity with Cosmos/SQL.

## 12. Open decisions for the repo owner

1. Version framing: 3.7.0 (conformance contract tightened) or 3.6.2 (suite treated as internal).
2. Repair packaging: throwaway script or `nb container reconcile-stale-pending` verb.
3. Whether §5.7 ships in the same PR (recommended, after the duplicate-detection check) or as its own.
4. Whether the `outcome_ignored` counter and the SQL must-not-skip CI gate go in the same PR.

## 13. Alternatives rejected

- **Resolver-side read-then-skip without touching the stores.** Atomic only while the session lock
  holds; not pinned by the conformance suite; needs `GetEventById` semantics changes in SQL and
  in-memory.
- **Transport fix (hold the lock, abandon instead of re-send).** Neutralises mechanism 2 only,
  cannot order auto-forwarding, rewrites throttle handling across three interfaces and relies on an
  isolated-worker abandon path nothing in the repo exercises. Follow-up, not a fix.
- **Version or generation stamp on the row.** Needs a SQL migration and a Cosmos read before every
  write including terminal ones; a rank rule was shown to refuse the supported
  Failed -> policy retry -> `PendingHandoffResponse` transition.
- **Timestamp or sequence-number guard.** The stale copy is the newer message on the Resolver topic;
  any time-ordered rule would have applied the overwrite.
- **Status-only predicate (refuse request copies only over terminal rows).** Leaves Pending+Handoff
  rows and control-request projections unprotected, and the first review draft's claim that the
  ancestor check covered them was wrong: a settlement's `ParentMessageId` is the handoff response's
  id, not the original request's.
- **Guarding only `EventRequest`.** The same reorder mechanisms delay `DeferralResponse` and
  `PendingHandoffResponse`; a late `DeferralResponse` after Completed leaves a row stuck as
  Deferred forever, which is worse than the incident.
- **`PatchItemAsync` with `FilterPredicate` in the store.** Needs new adapter overloads and is
  unproven on the emulator CI depends on; kept only as the atomic repair option.
- **New `TryUpload…` methods or an expected-status parameter.** Pushes policy into every caller and
  needs default-interface bridging; the existing `Task<bool>` already carries the signal.

## 14. Review trail

Adversarial review of the first draft (three independent passes, none refuting the approach) changed:
the rule now keys on what last wrote the row (`MessageType`) rather than status plus sub-status;
`DeferralResponse` and `PendingHandoffResponse` writes are guarded; `ScheduleRedelivery` keeps the
original `MessageId`; the Cosmos path fails closed on an unparseable status and throws on a lost
race instead of returning `false`; SQL gains `HOLDLOCK`, binary collation and `NULLIF` on the
ancestor compare, and an explicit `@@ROWCOUNT`; the in-memory store decides before stamping; refused
writes leave a Comment audit; test shape follows the file's `[TestMethod]`/`[DataRow]` and
one-container conventions, and the Resolver test helpers are hoisted first. A final reconciliation
pass corrected the topology description (both copies share the Nav09Endpoint fan-out, and the late
copy's dashed-GUID id identifies the reschedule path), widened the handoff-park rule to refuse only
Completed and Skipped, extended the same-id change to the DLQ replay, dropped the archive-then-copy
conformance case, and deferred the Cosmos create overload.
