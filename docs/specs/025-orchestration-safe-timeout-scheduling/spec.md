# Feature Specification: Orchestration-Safe Timeout Scheduling and Cancellation

Feature Branch: `feature/AF-117-adr-009-2-8-design-orchestration-safe-ti`
Created: 2026-07-22
Updated: 2026-07-22
Status: Approved design (ADR-009 series, 2/8) - implementation and verification land with AF-120
Input: AF-117 - "Design orchestration-safe timeout semantics for direct and outbox publishing"

## Problem

A process manager following [ADR-009](../../adr/009-orchestration-via-application-services.md)
and the [orchestration conventions](../../orchestration.md) needs workflow
timeouts that are correct on both publishing paths:

- Direct `ISender` scheduling returns a Service Bus sequence number and can
  cancel it.
- `OutboxSender` persists the schedule before Service Bus ever assigns a
  sequence number, returns `0` today, and cannot cancel after dispatch.

The process manager must remain correct across schedule/cancel races,
duplicate timeout delivery, crashes, redelivery, and completion immediately
before a timeout fires. This spec selects a minimal model built on stable
application timeout identity, deterministic MessageId, workflow-state guards,
and explicit lifecycle states for pending/dispatched/cancelled/fired timeout
records - without a generic workflow scheduler, a saga runtime, or a
distributed transaction across Service Bus and the application database.

File/line citations below were verified against `master` at commit `72d25d7`
(after AF-116 conventions and AF-118 context-aware workflow publishing merged).

## Revision 5 — resolutions to review findings

The selected model is unchanged: SQL-owned due-time dispatch with a provider-local handle and an atomic cancel-vs-dispatch-start fence for the transactional outbox (`SqlOwnedDueTime` mode behind the `ScheduledDeliveryMode` cutover gate), native Service Bus scheduling for direct sends, and application workflow-state/idempotency guards as the sole correctness boundary. All revision-2 resolutions (legacy long-only cancel stays NotSupported in outbox mode; TimeoutId↔sequence pair validation scoped to the SQL provider; phased delivery-mode cutover), revision-3 resolutions (logical ScheduledMessageId vs per-attempt transport MessageId preserved through every clone path; live reservations block session successors; best-effort lease budgeting against the SQL-returned deadline with permitted absorbed duplicate overlap; SQL-owned StoredAtUtc/OutboxSequenceNumber ordering authority; applock-serialized migration), and revision-4 resolutions (`IEventHandlerContext.ScheduledMessageId`/`ScheduledEnqueueTimeUtc` default members for typed handlers; CorrelationId preservation on retry clones of marked messages; the durable session-head predicate; mode-scoped pending/lag metrics; default-mode bit-for-bit selection and ordering parity) remain in force and are integrated below. The five revision-5 findings are resolved as follows, each verified against the current code:

1. **Operator resubmission loses timeout identity (error).** Verified chain: a terminal handler failure sends an ErrorResponse built by `ResponseService.CreateResponse` (`src/NimBus.Core/Messages/ResponseService.cs:132`), which carries no marker and sets `CorrelationId = messageContext.MessageId` (`:141`, the Resolver's response→message audit-linkage convention). The Resolver projects it into `MessageEntity` (`src/NimBus.Resolver/Services/ResolverService.cs:154`) and the failed-event record (`CreateUnresolvedEvent` `:242` → `UploadFailedMessage` `:332`); neither `MessageEntity` (`src/NimBus.MessageStore.Abstractions/MessageEntity.cs`) nor `UnresolvedEvent` (`src/NimBus.MessageStore.Abstractions/States/UnresolvedEvent.cs`) has marker fields. Resubmission reconstructs the message from those records — `ManagerClient.Resubmit` (`src/NimBus.Manager/ManagerClient.cs:66`, used by the WebApp's `AdminService.Resubmit` paths) and the CLI's `Container.Resubmit` (`src/NimBus.CommandLine/Container.cs:185`) — so the ResubmissionRequest reaches `StrictMessageHandler.HandleResubmissionRequest` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:193`) with `ScheduledMessageId` null and `CorrelationId` equal to the failed delivery's transport MessageId, making invariant 12's marker-keyed guard impossible. Resolution: **preserve identity through audit and resubmission** — resubmitting a terminally failed timeout stays supported (the Resolver's resubmit lever is the platform's core recovery story; after an operator fixes a handler bug, resubmit must let the workflow guard decide Fired vs IgnoredLate; prohibition is rejected in the alternatives table). Concretely: (a) wire — `CreateResponse` copies `ScheduledMessageId`/`ScheduledEnqueueTimeUtc` from the inbound context onto every Resolver-bound response, and for marked messages additionally stamps a new nullable response-only property `WorkflowCorrelationId = messageContext.CorrelationId` (the response's own `CorrelationId` keeps today's `= MessageId` convention untouched — the Resolver and WebApp flow view correlate by it); (b) store — `MessageEntity` and `UnresolvedEvent` gain nullable `ScheduledMessageId`, `ScheduledEnqueueTimeUtc`, and `WorkflowCorrelationId`; `ResolverService` projects them; Cosmos documents are additive; the SQL message store adds the columns via a new DbUp script plus `StoreMessage`/`MapMessageRow` updates (`src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs:185/:304`); (c) resubmission — for a marked entity, `ManagerClient.Resubmit` and `Container.Resubmit` stamp `ScheduledMessageId`/`ScheduledEnqueueTimeUtc` onto the ResubmissionRequest and set its `CorrelationId = WorkflowCorrelationId` (falling back to the entity's `CorrelationId` for pre-upgrade rows); unmarked entities keep today's construction byte-identical. Invariant 1 now names resubmission clones; tests include a terminal-failure→resubmit end-to-end proof.
2. **Expired started head can deadlock its session (error).** Confirmed against revision 4's own predicates: with DispatchStarted row A (lease expired) and backdated earlier-key row B, A fails ordering predicate (a) because B is an earlier due non-terminal row, while B fails head predicate (b) because A is in flight — neither row is claimable and the session is wedged, contradicting the stated expired-head behavior. Resolution: **ordering predicate (a) applies only to first claims** — it carries `AND c.[DispatchStartedAtUtc] IS NULL` on the candidate; a dispatch-started candidate (the session head being reclaimed after lease expiry) bypasses predecessor ordering entirely and is governed solely by lease expiry plus the head predicate, which already excludes the candidate row by Id. Rationale: a started row *is* the session's single in-flight slot and must terminalize before any ordering decision matters, regardless of what keys arrived meanwhile — the same rule invariant 10 already imposes on every other session row, now applied symmetrically to the head's own reclaim. A never-started expired reservation still re-enters ordering (its claim decision can safely be re-made). Invariant 10 and the claim eligibility rules are updated; the expired-head test now includes a backdated earlier-key row.
3. **Claim locking does not establish one session head (error).** Two defects fixed. (i) Hint validity: `READPAST` under `READ_COMMITTED_SNAPSHOT` is legal only when locking semantics are explicitly requested; the candidate scan pairs it with `UPDLOCK` — a locking read — which is the documented-valid combination, now stated explicitly. (ii) The real race: the session `NOT EXISTS` subqueries are versioned reads under RCSI (and non-range-locking reads under lock-based READ COMMITTED), so a backdated INSERT committing between predicate evaluation and the claim update could yield two live heads. Resolution: **serializable key-range locks on a dedicated session-ordering index.** The migration adds a persisted computed column `EffectiveDueAtUtc AS COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc) PERSISTED` and a nonclustered index on `(SessionId, EffectiveDueAtUtc, OutboxSequenceNumber)` including the claim/terminal columns; both session subqueries read `WITH (HOLDLOCK)` so they take key-range locks through that index — HOLDLOCK forces locking serializable semantics even when RCSI is on — and the range locks cover the insertion point of any earlier-keyed row. The claim is one `UPDATE … OUTPUT` autocommit statement, so those range locks are held until the claim's transaction commits: a concurrent backdated INSERT into a locked range blocks until the claim is committed and is then excluded by the now-committed head columns; if the INSERT commits first, the claim's subqueries see it. Either serialization order yields at most one live head. The computed column also makes the ordering predicates sargable. Tests: an insertion-boundary barrier test (concurrent backdated INSERT vs claim, asserting one live head) executed with `READ_COMMITTED_SNAPSHOT` both OFF and ON via `ALTER DATABASE` in the fixture (the CI container defaults OFF; Azure SQL defaults ON), and claim retry on deadlock (error 1205) is documented dispatcher behavior.
4. **Dispatcher mode selection contract is undefined (error).** Verified: `AddNimBusOutboxDispatcher` constructs `OutboxDispatcher` from `IOutbox` + sender only (`src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:312–332`) and `AddNimBusSqlServerOutbox` eagerly builds one `SqlServerOutbox` singleton at registration time (`src/NimBus.Outbox.SqlServer/ServiceCollectionExtensions.cs:24–38`); `ScheduledDeliveryMode` lives in provider-local options, so coordinator *presence* cannot distinguish the legacy fallback from the new protocol. Resolution: **the capability travels on the interface, not on registration.** `IOutboxDispatchCoordinator` exposes `bool DueTimeDispatchActive { get; }`; `SqlServerOutbox` returns `ScheduledDelivery == SqlOwnedDueTime`. Registration stays unconditional (`TryAddSingleton` of the same instance for `IScheduledOutbox` and `IOutboxDispatchCoordinator`), so DI composition is independent of configuration timing. The pinned selection rule: `OutboxDispatcher` gains a constructor overload accepting an optional `IOutboxDispatchCoordinator` (existing constructors preserved) and runs the claim/fence/checkpoint protocol iff `coordinator is { DueTimeDispatchActive: true }`; in every other case — no coordinator registered (custom `IOutbox` providers) or coordinator reporting default mode — the legacy `GetPendingAsync`/`MarkAsDispatchedAsync` flow runs unchanged. Complete companion signatures are specified in the schema/protocol section so the implementation cannot improvise them. Three pinned unit tests: no coordinator → legacy path; coordinator inactive → legacy path and the claim API is never called; coordinator active → claim protocol and `GetPendingAsync` is never called.
5. **Lease margin can eliminate the send window (warning).** No invariant bounded `SendLeaseSafetyMargin`; a margin ≥ `SendLeaseDuration` closes the budgeted window at dispatch start and cancels every send attempt, retrying the row and its session forever. Resolution: options invariants are validated wherever `SqlServerOutboxOptions` is consumed (the `AddNimBusSqlServerOutbox` configure path and the `SqlServerOutbox` constructor, matching the existing eager `ConnectionString` check): `SendLeaseDuration` must be positive and at most 24 hours, and `TimeSpan.Zero <= SendLeaseSafetyMargin < SendLeaseDuration`, throwing `ArgumentOutOfRangeException` naming the offending property. The effective send budget (SQL-returned deadline minus margin) is therefore always a positive window at start-fence time. Unit tests cover margin equal to, greater than, and one tick below the duration, a negative margin, and a non-positive duration.

Revision-4 resolutions retained verbatim in the body (context for the sections below):

1. **Timeout identity is unavailable to typed handlers (error).** Application handlers implement `IEventHandler<T>.Handle(T, IEventHandlerContext, CancellationToken)` and never see `IMessageContext` (verified: `src/NimBus.SDK/EventHandlers/IEventHandler.cs:19`; `IEventHandlerContext` currently exposes only MessageId/EventId/EventType/CorrelationId plus handoff members). Resolution: add **backward-compatible default interface members** to `IEventHandlerContext` — `string ScheduledMessageId => null;` and `DateTimeOffset? ScheduledEnqueueTimeUtc => null;` — the same forward-compatibility technique the interface already uses for `GetCloudEvent()` (`src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs:78`), so external implementers stay source- and binary-compatible. `EventHandlerContext` gets settable properties that default from the wrapped `IMessageContext` when constructed with one (both existing constructors preserved; the parameterless one leaves them null-until-set like its other properties). `EventJsonHandler<T>`'s object initializer (`src/NimBus.SDK/EventHandlers/EventJsonHandler.cs:46–55`) populates both from the context. `DelegateEventJsonHandler` and dynamic/fallback handlers receive `IMessageContext` directly (verified `DelegateEventJsonHandler.cs:30`) and therefore read the marker from `IMessageContext`, which this plan already extends; `MarkPendingHandoffJsonHandler` needs no change. Invariant 12's CAS is now executable from a typed handler using the logical identity, never the per-attempt MessageId. Tests: `EventJsonHandlerTests` unit coverage (marker present → exposed; absent → null), plus end-to-end assertions that the handler context exposes the original ScheduledMessageId after a RetryRequest redelivery and after a deferred park/republish.
2. **Timeout retries replace the workflow correlation ID (error).** Verified: `ResponseService.CreateRetryResponse` sets `CorrelationId = messageContext.MessageId` (`src/NimBus.Core/Messages/ResponseService.cs:181`), while `SendToDeferredSubscription` already preserves `messageContext.CorrelationId` (`:227`). A RetryRequest is redelivered to the same subscriber and `StrictMessageHandler.HandleRetryRequest` runs the user handler with that clone's context (`src/NimBus.Core/Messages/StrictMessageHandler.cs`), so after one failure a timeout handler would see the timeout's transport MessageId as its CorrelationId and AF-118's `PublishFromContext` would propagate it. Resolution: **scope CorrelationId preservation to scheduled messages** — when the inbound context carries the `ScheduledMessageId` marker, `CreateRetryResponse` copies `messageContext.CorrelationId` onto the clone (exactly the deferred-park behavior); for unmarked messages the method stays byte-identical to today, because ordinary retry flows and the Resolver/WebApp flow view rely on the existing `CorrelationId = parent MessageId` convention and `ParentMessageId` already carries the parent linkage. Because every retry clone of a marked message preserves the inbound CorrelationId, second and later retries keep the persisted workflow conversation ID by induction. Rejected alternative: a separate workflow-correlation marker property — it adds a second source of truth for a value CorrelationId is defined to carry under AF-116/AF-118 conventions. Tests: ResponseServiceTests pin (a) marked context → clone CorrelationId equals the inbound workflow CorrelationId, ParentMessageId equals the inbound MessageId, fresh transport identity; (b) unmarked context → unchanged legacy behavior; (c) an end-to-end timeout that fails twice and still sees the workflow conversation ID and original ScheduledMessageId on the second retry.
3. **Backdated rows can bypass an active session head (error).** Past-due schedules are legal and order by `COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc)`, so a row B inserted with an earlier ordering key than an already Reserved/DispatchStarted row A has no earlier predecessor and, under revision 3's predicate alone, a second worker could start B concurrently with A. Resolution: add a **durable session-head rule** to the claim predicate. A session's head is any row that is *in flight*: `DispatchStartedAtUtc IS NOT NULL` and non-terminal (any lease state), or reserved under a live claim (`DispatchClaimId IS NOT NULL AND DispatchClaimedUntilUtc > SYSUTCDATETIME()`). While a head exists, **no other row of that session is claimable by anyone, regardless of ordering key**; only the head row itself may be continued by its owner or reclaimed after lease expiry (the predicate excludes the candidate row by Id, which is precisely what permits same-row reclamation). The head is durable because it is carried entirely in committed columns (`DispatchStartedAtUtc`, `DispatchClaimId`, `DispatchClaimedUntilUtc`) — no new column and no in-memory state. A Reserved row whose lease expires without dispatch-start ceases to be head (it is no longer in flight; the earlier-predecessor rule still governs ordering around it). Combined with the revision-3 earlier-predecessor predicate this yields: at most one in-flight row per session; earliest-due-first among claimable rows; and a backdated insert cannot run concurrently with the active head — it dispatches after the head terminalizes and before later-keyed successors, which invariant 10 now states explicitly. Both session subqueries are correlated `NOT EXISTS` with **no READPAST**. Tests: backdated-insert-after-reserve and backdated-insert-after-start two-worker barrier tests (B not claimable while A is head), expired-head reclamation (only A reclaimable; B and A's successors stay blocked until A terminalizes), and a backdated-goes-next ordering test after the head terminalizes.
4. **Scheduled dispatch lag uses the storage timestamp (warning).** Verified today: `GetOldestPendingEnqueuedAtUtcAsync` returns the oldest pending `CreatedAtUtc` (`src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs:273`) and the gauge computes `lag = now − oldest` (`src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs:186`). Revision 3's switch to `StoredAtUtc` would make a month-ahead timeout report ~a month of lag the moment it becomes due. Resolution: **pending/lag metrics are mode-specific.** In `SqlOwnedDueTime`, the oldest-pending baseline is `MIN(COALESCE([ScheduledEnqueueTimeUtc], [StoredAtUtc]))` over **dispatch-eligible** rows only (non-terminal, not cancelled, due or unscheduled) — a future timeout contributes nothing until due, and once due its lag counts from its due time; the pending-count gauge is scoped the same way. In `BrokerScheduleAtDispatch`, keep today's exact semantics (oldest `CreatedAtUtc` over all `DispatchedAtUtc IS NULL` rows) because future-scheduled rows are immediately actionable there (eager broker scheduling at dispatch). Tests: a due month-ahead row reports ≈0 lag in `SqlOwnedDueTime`; a not-yet-due row contributes neither pending count nor lag; default-mode gauge parity with today.
5. **Default-mode ordering is not bit-for-bit compatible (warning).** Verified today: `GetPendingAsync` selects `WHERE [DispatchedAtUtc] IS NULL ORDER BY [CreatedAtUtc] ASC` (`src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs:172–178`). Resolution: **the legacy query and ordering are retained verbatim in `BrokerScheduleAtDispatch` mode** — same selection, same `ORDER BY [CreatedAtUtc] ASC` — so an upgraded default-mode dispatcher selects exactly the row an old dispatcher would. The single addition is `[CancelledAtUtc] IS NULL`, which is vacuously true for every row default mode can produce (cancellation is impossible in default mode) and exists only as a safety guard so a misconfigured mode-downgrade after cancellations cannot dispatch a cancelled row; docs call the downgrade itself out as a misconfiguration. All new ordering (`COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc), OutboxSequenceNumber`), due-time eligibility, claims/leases, and session predicates are scoped exclusively to the `SqlOwnedDueTime` claim queries. `StoredAtUtc` and `OutboxSequenceNumber` remain additive columns that default mode writes but never reads for ordering. The default-mode parity test now includes ordering: rows inserted with deliberately out-of-order `CreatedAtUtc` values (relative to insertion order, so StoredAtUtc/sequence order differs) must dispatch in `CreatedAtUtc` order, matching an old-binary-shaped reader run against the same table.

## Decision and scope

Select a stable timeout handle plus application-owned workflow guards, with two transport implementations:

- Direct publishing continues to use Azure Service Bus native scheduling. The handle contains the application timeout identity and the broker sequence number; only the SQL provider can validate the identity↔sequence pairing.
- SQL-outbox publishing, in `SqlOwnedDueTime` mode, owns the due time until it expires. The scheduled row stays in SQL, receives a provider-local non-zero sequence number, and is sent normally only when due. Cancellation can therefore be linearized in SQL before dispatch starts; the outbox does not eagerly create a broker schedule and does not need a second broker-sequence checkpoint/cancellation worker. In the default `BrokerScheduleAtDispatch` mode, behavior is unchanged from today bit-for-bit — including row selection and `CreatedAtUtc` ordering (revision-4 finding 5): eager broker scheduling at dispatch, legacy `Schedule` returns 0, cancellation unsupported.
- In both modes the logical timeout identity **TimeoutId** is stamped as the deterministic NimBus MessageId of the timeout's **first** delivery and as the `ScheduledMessageId` marker property on every representation of the timeout, including retry and deferred clones which carry their own per-attempt transport MessageIds. SessionId is the workflow ID, CorrelationId is the persisted workflow conversation ID — retry clones of a marked timeout preserve it (revision-4 finding 2) and operator resubmission restores it from the audit store (revision-5 finding 1) — and parent/origin lineage comes from AF-118. Typed handlers read the logical identity from `IEventHandlerContext` (revision-4 finding 1).
- Cancellation only suppresses work when its transport-specific race is won. Application workflow state, processed-message idempotency, and optimistic concurrency remain the correctness boundary. No NimBus saga state, scheduler product, workflow repository, or distributed transaction is introduced.

The SQL-outbox timing contract becomes "not before DueAtUtc; delivery can be late while the dispatcher is unavailable." Direct mode retains broker-managed timing. This difference must be documented rather than hidden.

## Alternatives evaluated

| Model | Decision | Reason |
| --- | --- | --- |
| Keep the current long broker-sequence-only contract | Reject | The outbox has no broker sequence before dispatch, currently returns 0, cannot identify/cancel its durable row, and a broker sequence carries no stable application timeout identity. |
| Eagerly call Service Bus ScheduleMessage from the outbox, then persist its broker sequence | Reject | A crash after broker acceptance and before sequence checkpoint creates an uncancellable orphan schedule. Closing that gap requires two identifiers, a durable cancellation worker, and additional compensating states, while still not producing an atomic SQL/Service Bus transition. |
| Hold a SQL transaction or row lock across the broker call | Reject | It consumes locks/connections across network I/O and still cannot atomically commit SQL with Service Bus. |
| Delete pending rows without a dispatch fence | Reject | A dispatcher can already have selected the row; deletion loses audit intent and cannot decide the cancel/send race. |
| Sequence-only provider cancellation for the obsolete long bridge | Reject | It cannot validate timeout identity, needs weaker parallel outcome semantics, and today's bridge already throws NotSupported in outbox mode — keeping it unsupported is zero regression. |
| Durable broker-sequence→MessageId map for direct pair validation | Reject | A second store with its own write/crash races just to harden an optimization; direct cancellation stays sequence-only best effort, documented. |
| Reuse the original transport MessageId on retries to keep identity | Reject | Service Bus duplicate detection (where enabled) would silently drop the retry as a duplicate of the original delivery. Logical identity must ride in an application property, not the transport MessageId. |
| Enforce the lease boundary with a hard send timeout | Reject | Cooperative cancellation cannot bound a suspended process or an ignoring sender; claiming enforcement would be dishonest. Budget best-effort against the SQL-returned deadline and permit absorbed duplicate overlap instead. |
| Keep CreatedAtUtc as the eligibility/ordering key in `SqlOwnedDueTime` | Reject | It is application-stamped: positive skew delays immediate rows, equal/regressed values plus random Id ordering can reorder same-session rows. SQL-owned StoredAtUtc + IDENTITY sequence are authoritative there; CreatedAtUtc ordering survives only in default mode for bit-for-bit parity. |
| Preserve CorrelationId on **every** RetryRequest clone | Reject (new) | Unmarked messages keep today's `CorrelationId = parent MessageId` convention that the Resolver flow view and existing consumers rely on; changing it globally is an unforced behavioral break. Preservation is scoped to marker-carrying scheduled messages. |
| Carry a separate workflow-correlation marker property instead of preserving CorrelationId | Reject (new) | Two sources of truth for the conversation ID; AF-116/AF-118 already define CorrelationId as the workflow conversation ID, and the deferred-park path already preserves it. |
| Forbid or clamp backdated due times at insert | Reject (new) | Past-due schedules are semantically valid (\"fire as soon as possible\") and clamping cannot close the race anyway — an in-flight head can coexist with any not-yet-visible insert. The durable session-head predicate closes it at the claim site. |
| Expose ScheduledMessageId to typed handlers via a new handler interface or context downcast | Reject (new) | A parallel handler interface fragments the SDK; downcasting to `EventHandlerContext` breaks custom `IEventHandlerContext` implementations. Default interface members are the established forward-compat pattern in this exact interface (`GetCloudEvent`). |
| One mode-independent pending/metrics query | Reject (new) | It either breaks default-mode bit-for-bit parity (finding 5) or reports month-long phantom lag for future timeouts (finding 4). Queries and gauges are mode-scoped. |
| Prohibit resubmission of terminally failed timeouts | Reject (rev 5) | Resubmit is the Resolver's core recovery lever; after an operator fixes a handler bug, resubmission must let the workflow guard decide Fired vs IgnoredLate. Identity is preserved through the audit chain instead. |
| Per-session sp_getapplock around the claim | Reject (rev 5) | Adds applock resource management and serializes claim rounds beyond what is needed; index key-range locks on committed columns give the same one-live-head guarantee inside the existing single-statement claim. |
| Dedicated session-head table with a unique SessionId constraint | Reject (rev 5) | A second table with its own insert/delete lifecycle and crash-cleanup protocol; the head state already lives in committed columns and the range-locked predicates enforce it. |
| Conditional coordinator registration keyed on options at registration time | Reject (rev 5) | Ties protocol selection to configuration evaluation order and hides the mode from the dispatcher; the capability signal on the interface keeps registration unconditional and the selection rule testable. |
| Add a separate generic scheduler/saga store | Reject | It duplicates the outbox and application workflow state and violates ADR-009 and the stated non-goals. |
| SQL-owned due time, local handle, lease with SQL-returned deadline, cancel-vs-start CAS, SQL-owned ordering, durable session head, applock-serialized migration, behind an explicit delivery-mode cutover | Select | One durable pre-dispatch authority, deterministic pending cancellation, no orphan broker schedules, at-least-once recovery with honestly-documented duplicate overlap, at most one in-flight row per session even under backdated inserts, safe rolling upgrade, and the smallest model that gives the direct and built-in SQL paths honest behavior. |

## Public contract

Add public types under src/NimBus.Core/Messages:

~~~csharp
public enum ScheduledMessageHandleKind
{
    BrokerSequenceNumber = 0,
    SqlOutboxSequenceNumber = 1,
}

public sealed record ScheduledMessageHandle(
    string TimeoutId,
    long SequenceNumber,
    ScheduledMessageHandleKind Kind);

public enum ScheduledMessageCancellationOutcome
{
    CancellationRequested = 0,
    CancelledBeforeDispatch = 1,
    AlreadyCancelled = 2,
    TooLate = 3,
    NotFound = 4,
    Unsupported = 5,
}
~~~

TimeoutId is the logical timeout identity. It equals the deterministic MessageId of the first delivery and the `ScheduledMessageId` marker on all deliveries; it is not independently configurable. SequenceNumber is opaque and valid only with the same endpoint-bound publisher configuration that created the handle. It is a Service Bus sequence in direct mode and a SQL-outbox-local sequence in outbox mode. Callers must persist the handle they receive; NimBus never reconstructs or looks up a handle from TimeoutId alone.

Cancellation outcome semantics per mode:

- Direct (`BrokerSequenceNumber`): success returns `CancellationRequested` only. The broker API is sequence-only; NimBus validates kind and shape but **cannot** verify that the TimeoutId in the handle matches the broker sequence — a mismatched pair cancels whatever sequence was supplied. Broker errors (e.g. already-activated) surface as the underlying `ServiceBusException`.
- SQL outbox (`SqlOutboxSequenceNumber`): the provider CAS matches sequence **and** TimeoutId **and** scheduled-ness, so a forged/mistyped handle affects zero rows and returns `NotFound`. Success returns the precise outcome (`CancelledBeforeDispatch`, `AlreadyCancelled`, `TooLate`, `NotFound`).
- Legacy long-only path in outbox mode: `Unsupported` (the obsolete bridge keeps throwing `NotSupportedException`; see compatibility rules).

After AF-118 lands, add these exact workflow-facing members to IPublisherClient and implement them in PublisherClient by reusing AF-118's context-aware message factory:

~~~csharp
Task<ScheduledMessageHandle> Schedule(
    IEvent @event,
    DateTimeOffset scheduledEnqueueTime,
    IEventHandlerContext context,
    string timeoutId,
    CancellationToken cancellationToken = default);

Task<ScheduledMessageCancellationOutcome> CancelScheduled(
    ScheduledMessageHandle handle,
    CancellationToken cancellationToken = default);
~~~

The schedule overload must validate the event, require a nonblank TimeoutId of at most the Service Bus MessageId limit (128 characters), stamp it as both MessageId and the ScheduledMessageId marker, preserve AF-118 SessionId/CorrelationId/ParentMessageId/OriginatingMessageId/OriginatingFrom metadata, preserve native and CloudEvents envelopes, and mark the message as scheduled with its due time. A past due time is allowed and means immediately eligible; DateTimeOffset is normalized to UTC.

**Handler-facing contract (revision-4 finding 1).** `IEventHandlerContext` gains backward-compatible default interface members so timeout handlers can read the logical identity and due time without touching `IMessageContext`:

~~~csharp
/// <summary>Logical scheduled-message identity (TimeoutId); stable across retries,
/// deferred replay, and redelivery. Null for ordinary messages.</summary>
string ScheduledMessageId => null;

/// <summary>Original scheduled due time. Null for ordinary messages.</summary>
DateTimeOffset? ScheduledEnqueueTimeUtc => null;
~~~

`EventHandlerContext` implements both as settable properties defaulting from its wrapped `IMessageContext`; `EventJsonHandler<T>` populates them in its existing initializer. Dynamic (`IEventJsonHandler`) handlers read the same values from `IMessageContext` directly. Invariant 12's CAS and `ReportScheduledMessageOutcome` (telemetry section) therefore key on `context.ScheduledMessageId` in application code.

Compatibility rules:

- Add default NotSupported implementations to IPublisherClient so existing external implementations and in-tree doubles such as Crm.Api/NoopPublisherClient remain source/binary compatible.
- Keep ISender.ScheduleMessage(IMessage, DateTimeOffset, CancellationToken) and CancelScheduledMessage(long, CancellationToken) unchanged.
- Add default richer ISender overloads ScheduleMessageWithHandle and CancelScheduledMessage(ScheduledMessageHandle, ...) that bridge to the existing long methods for direct/custom senders. InstrumentingSenderDecorator must explicitly forward the richer overloads so it does not hide OutboxSender's implementation.
- Keep PublisherClient.Schedule(IEvent, DateTimeOffset) and CancelScheduled(long) with their exact signatures as Obsolete compatibility bridges. Direct behavior is unchanged. In outbox mode: `Schedule` returns the non-zero provider-local sequence when `SqlOwnedDueTime` mode is active (0 as today otherwise); `CancelScheduled(long)` remains NotSupported in outbox mode in **all** modes — the long alone cannot carry TimeoutId, and the SQL CAS requires both. The documented migration is to the handle API.
- The new `IEventHandlerContext` members are default interface members returning null; existing custom implementations compile and behave unchanged. `MessageContextStub` in tests/NimBus.SDK.Tests and other in-tree doubles pick up the defaults.
- Do not obsolete the low-level ISender long methods: internal retry/redelivery and transport implementations still use that primitive.
- Reject invalid enum kinds, non-positive sequence values, a blank TimeoutId, or use through the wrong sender kind; do not silently reinterpret an outbox sequence as a broker sequence. TimeoutId↔sequence **pair** validation is enforced only where it is enforceable — the SQL provider. Direct mode's shape-only validation is documented at the API.

## Authoritative invariants

1. A logical timeout has one immutable TimeoutId. TimeoutId equals the transport MessageId of the timeout's **first** delivery and is carried as the `ScheduledMessageId` marker property on **every** representation of that timeout — including RetryRequest clones, deferred parks and republishes, throttle redeliveries, Resolver-bound responses, the persisted audit/failed-event records, and operator ResubmissionRequests reconstructed from them — each of which mints its own per-attempt transport MessageId (reusing the original would trip broker duplicate detection and drop the retry). Every clone of a marker-carrying message also preserves the workflow conversation ID: `SendToDeferredSubscription` and (for marked messages) `CreateRetryResponse` preserve the inbound **CorrelationId** directly; Resolver-bound responses carry it in the response-only `WorkflowCorrelationId` property (their own CorrelationId keeps the `= MessageId` audit-linkage convention), from which resubmission restores it onto the clone's CorrelationId. Unmarked messages stay byte-identical on every path. A newly armed generation gets a new deterministic TimeoutId.
2. A committed SQL-outbox schedule has exactly one non-zero provider-local handle. An ambient transaction rollback leaves neither the application state mutation nor a cancellable row; identity gaps are allowed.
3. SQL Server is the authority for SQL-outbox eligibility and ordering **in `SqlOwnedDueTime` mode**. A null ScheduledEnqueueTimeUtc is immediately eligible by definition; a non-null one is compared against SYSUTCDATETIME() and never dispatch-eligible early. Ordering uses SQL-assigned StoredAtUtc and OutboxSequenceNumber (COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc), then OutboxSequenceNumber); application-stamped CreatedAtUtc never gates or orders dispatch in this mode. In `BrokerScheduleAtDispatch` mode, selection and ordering remain today's exact `WHERE DispatchedAtUtc IS NULL … ORDER BY CreatedAtUtc ASC` for bit-for-bit parity with pre-upgrade dispatchers.
4. Cancellation is an optimization. A direct broker-cancel call that returns successfully means CancellationRequested, not proof that activation cannot race. Azure Service Bus activation and cancellation are independent operations, and direct cancellation cannot validate the TimeoutId↔sequence pairing.
5. In SQL, CancelledAtUtc and the first DispatchStartedAtUtc are mutually exclusive race winners. One parameterized conditional UPDATE decides the race; no in-memory read decides it.
6. Merely reserving/claiming an outbox row does not close cancellation. Cancellation remains possible until the dispatcher's just-before-I/O dispatch-start fence wins.
7. A row whose SQL cancellation transition wins is never sent **by an upgraded dispatcher in `SqlOwnedDueTime` mode**. Once dispatch-start wins, cancellation returns TooLate even if the broker operation later fails or its result is ambiguous. This invariant is claimed only after full cutover: the mode flip asserts no pre-upgrade dispatcher runs against the table.
8. Claims and leases prevent healthy workers from starting the same unstarted row concurrently, but do not claim exactly-once transport delivery. A crash or unknown outcome after send can replay the row with the same MessageId.
9. Every claim/start/complete/release mutation is conditioned on row ID plus claim owner, and start also requires an unexpired lease. The dispatch-start CAS atomically extends the owner's lease (`SYSUTCDATETIME() + SendLeaseDuration`) and **returns the SQL-computed lease deadline**; the dispatcher budgets its bounded send against that authoritative deadline minus a safety margin. The bound is **best effort**: a sender that ignores cancellation or a suspended process can outlive the lease, after which another worker may reclaim and retry the row while the stale attempt is still in flight. Overlapping duplicate attempts of the same row are explicitly permitted; they carry the same transport MessageId and are absorbed by application idempotency (and broker duplicate detection where enabled). What is guaranteed is checkpoint exclusivity: a stale owner's complete/release affects zero rows, so exactly one attempt terminalizes the row.
10. A session has at most one in-flight row, and due rows preserve deterministic per-session ordering for **first deliveries**. Two predicates enforce this, both correlated `NOT EXISTS` subqueries that never use READPAST, both over committed columns only: (a) **ordering, first claims only (revision 5)** — a row that has **not** yet dispatch-started (`c.[DispatchStartedAtUtc] IS NULL`) is not claimable while an earlier due non-terminal row of its session exists, in any claim state (unclaimed, live-reserved, expired-claim, dispatch-started), where "earlier" uses the SQL-owned ordering authority (invariant 3) and a not-yet-due future row is exempt; a dispatch-started candidate — the session head being reclaimed after lease expiry — bypasses (a) entirely, because the started row is the session's in-flight slot and must terminalize before ordering can matter, no matter what keys arrived meanwhile; (b) **session head (revision 4)** — a row is not claimable while any *other* row of its session is in flight, meaning dispatch-started and non-terminal (any lease state) or reserved under a live unexpired claim, **regardless of ordering key** — this is what stops a backdated earlier-key insert from running concurrently with the active head. Only the head row itself may be continued by its owner or reclaimed after lease expiry; the head dissolves only when the row terminalizes (dispatched/cancelled) or, for a never-started reservation, when its lease expires. A backdated insert therefore dispatches after the current head terminalizes and before later-keyed successors — and an expired started head plus a backdated earlier-key row can never wedge the session: the head bypasses (a), is reclaimed, terminalizes, and only then does (a) admit the backdated row. Both predicates are evaluated under serializable key-range locks (`HOLDLOCK`) on the session-ordering index (revision 5), held for the duration of the single-statement claim transaction, so a concurrent backdated insert cannot slip between predicate evaluation and the claim update — in either commit order at most one live head exists per session, under lock-based READ COMMITTED and under READ_COMMITTED_SNAPSHOT alike. After an ambiguous send attempt (client timeout, unknown broker outcome, lease-expired overlap), a duplicate of an earlier row may still arrive after a later row; that reorder is confined to duplicates and is absorbed by the idempotency guard.
11. Duplicate cancellation is idempotent in SQL mode. Unknown, rolled-back, purged, nonscheduled, dispatch-started, and dispatched handles return explicit outcomes and never affect another row. Direct mode repeats the best-effort broker request or surfaces the broker failure.
12. A timeout handler must re-read application state and processed-message data, then compare Pending status plus the current TimeoutId/generation under optimistic concurrency, **keying on the ScheduledMessageId marker — read from `IEventHandlerContext.ScheduledMessageId` in typed handlers or `IMessageContext` in dynamic handlers — never on the per-attempt transport MessageId**. Only that compare-and-set may apply timeout effects.
13. Workflow completion and timeout firing have one durable race winner. The loser reloads after a version conflict or sees a nonmatching state/TimeoutId and completes as an observable no-op.
14. Resolver/WebApp remains operational message history. A ResolutionResponse Completed status means the timeout message was handled; it does not say whether the business timeout won.
15. No correctness claim relies on Service Bus duplicate detection. Same-row duplicates (redelivery, lease-expired overlap) share a transport MessageId, so duplicate detection helps where enabled; retry-generation attempts intentionally differ in transport MessageId and are absorbed only by the ScheduledMessageId-keyed application guard, which remains mandatory in all cases.

## State diagrams

Direct path:

~~~mermaid
stateDiagram-v2
    [*] --> AppPending: persist TimeoutId + due + generation
    AppPending --> BrokerScheduled: Schedule succeeds / broker sequence returned
    AppPending --> Reconcile: crash before or around Schedule
    Reconcile --> BrokerScheduled: retry with same MessageId
    BrokerScheduled --> CancellationRequested: completion commits, then cancel requested
    BrokerScheduled --> Received: due-time activation wins
    CancellationRequested --> Received: activation/cancel race may still deliver
    Received --> Fired: Pending + matching TimeoutId CAS wins
    Received --> IgnoredLate: completed/cancelled/superseded/duplicate
    Fired --> [*]
    IgnoredLate --> [*]
~~~

Direct recovery is explicitly two-phase because no transaction spans workflow storage and Service Bus: persist Pending first, schedule after commit, then persist the returned broker sequence. Missing or ambiguous sequence state is reconciled by rescheduling the same MessageId; duplicate delivery remains stale-safe.

SQL-outbox path (`SqlOwnedDueTime` mode):

~~~mermaid
stateDiagram-v2
    [*] --> Pending: workflow state + scheduled row commit together
    [*] --> Absent: ambient SQL transaction rolls back
    Pending --> Reserved: due-row claim with owner + lease (row becomes session head)
    Reserved --> Cancelled: cancel CAS wins before start
    Pending --> Cancelled: cancel CAS wins
    Reserved --> Pending: lease expires before start (head dissolves)
    Reserved --> DispatchStarted: start CAS wins + lease extended, SQL deadline returned
    DispatchStarted --> Dispatched: Send succeeds + owned checkpoint
    DispatchStarted --> Retryable: bounded-send timeout, failure, crash, or unknown outcome
    Retryable --> Reserved: lease expires / owned retry of the SAME row (stale attempt may still be in flight)
    Dispatched --> Received
    Received --> Fired: application Pending + matching ScheduledMessageId CAS wins
    Received --> IgnoredLate: duplicate/completed/cancelled/superseded
    Cancelled --> [*]
    Fired --> [*]
    IgnoredLate --> [*]
~~~

The outbox dispatcher uses Send, not ScheduleMessage, once a row is due. Therefore there is no broker-scheduled state or broker sequence to checkpoint in outbox mode. Every non-terminal state — Pending, Reserved, DispatchStarted, Retryable — blocks same-session successors; a live-Reserved or DispatchStarted row is additionally the session **head** and blocks every other session row in both key directions (invariant 10b), so a backdated insert waits for it. A started head whose lease expires is reclaimed via the ordering-bypass rule (invariant 10a applies only to not-yet-started candidates), so a backdated arrival can never wedge the session against its own head.

## Rolling upgrade and delivery-mode cutover

`SqlServerOutboxOptions` gains:

~~~csharp
public enum ScheduledDeliveryMode
{
    BrokerScheduleAtDispatch = 0, // default: today's behavior, bit for bit — including CreatedAtUtc selection/ordering
    SqlOwnedDueTime = 1,          // new protocol: due-time ownership, claims, fences, session head, handles
}

public ScheduledDeliveryMode ScheduledDelivery { get; set; } = ScheduledDeliveryMode.BrokerScheduleAtDispatch;
public TimeSpan SendLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);      // per-attempt send window; validated: positive, at most 24h
public TimeSpan SendLeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(5);   // subtracted from the SQL-returned deadline; validated: 0 <= margin < SendLeaseDuration
~~~

Both lease options are validated where the options object is consumed — the `AddNimBusSqlServerOutbox` configure path and the `SqlServerOutbox` constructor — with `ArgumentOutOfRangeException` naming the offending property (revision-5 finding 5). `SendLeaseDuration` must be positive and at most 24 hours; `SendLeaseSafetyMargin` must be nonnegative and strictly smaller than `SendLeaseDuration`, so the budgeted send window (SQL-returned deadline minus margin) is always positive at start-fence time. A misconfiguration fails fast at startup instead of silently cancelling every send attempt.

Why a mode gate: an old dispatcher binary knows nothing of CancelledAtUtc, claims, or SQL-owned due time — its `GetPendingAsync` (`WHERE DispatchedAtUtc IS NULL`, `CreatedAtUtc` oldest-first) would claim new-style rows, eagerly broker-schedule them, and could send a row *after* a new process returned `CancelledBeforeDispatch`. There is no server-side way to hide rows from an old binary's SQL, so the fence must be operational and explicit:

- Phase 1 — upgrade binaries everywhere with the default mode. Zero behavior change: schedule stores the row as today, dispatchers (old or new) select and order rows identically — the upgraded binary keeps the legacy query text and `ORDER BY [CreatedAtUtc] ASC` in this mode (revision-4 finding 5), so old and new dispatchers pick the same row given the same table state — eagerly broker-schedule at dispatch, legacy `Schedule` returns 0, cancellation stays unsupported. The schema migration is additive, applock-serialized against concurrent starters, and ignored by old readers.
- Phase 2 — once no pre-upgrade dispatcher process remains, set `ScheduledDelivery = SqlOwnedDueTime` on every publisher and dispatcher host. The flip is the operator's assertion of full cutover; docs state the `CancelledBeforeDispatch` guarantee (invariant 7) holds only from this point.
- Phase 3 — applications adopt the handle-based Schedule/Cancel API. `StoreScheduledAsync`/handle cancellation throw `InvalidOperationException` naming the required mode if invoked in `BrokerScheduleAtDispatch` mode, so the API cannot silently produce rows an old fleet might mishandle.

Mixed-fleet residual (documented and tested): in `SqlOwnedDueTime` mode, upgraded dispatchers of *different point versions* still interoperate through the claim/lease columns; duplicates from claim races or lease expiry are absorbed per invariant 9/15. Config skew inside phase 2 (one host flipped, one not) degrades to at-least-once with possible eager broker scheduling by the unflipped host — correctness of the *application* guard is unaffected, only cancellation precision is; docs call this out as a misconfiguration to converge quickly. Flipping **back** to `BrokerScheduleAtDispatch` after cancellations or future-due rows exist is likewise a documented misconfiguration; the default-mode query's `CancelledAtUtc IS NULL` guard ensures even then a cancelled row is never dispatched (future-due rows would dispatch eagerly, which is default-mode semantics).

## SQL schema and dispatcher protocol

Implement optional companion contracts rather than adding required abstract members to IOutbox. Complete signatures (revision-5 finding 4), in src/NimBus.Core/Outbox:

~~~csharp
public interface IScheduledOutbox
{
    /// <summary>Stores a scheduled outbox row inside the ambient transaction (when one
    /// exists) and returns the provider-local sequence number. The message carries
    /// ScheduledMessageId (TimeoutId) and ScheduledEnqueueTimeUtc. Throws
    /// InvalidOperationException naming the required mode unless SqlOwnedDueTime is active.</summary>
    Task<long> StoreScheduledAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>CAS-cancels a pending scheduled row by handle (sequence + TimeoutId +
    /// scheduled-ness). Honors the ambient transaction. Throws InvalidOperationException
    /// naming the required mode unless SqlOwnedDueTime is active.</summary>
    Task<ScheduledMessageCancellationOutcome> CancelScheduledAsync(ScheduledMessageHandle handle, CancellationToken cancellationToken = default);
}

public interface IOutboxDispatchCoordinator
{
    /// <summary>Provider-neutral protocol signal: true when the provider owns due-time
    /// dispatch (SqlOwnedDueTime). When false, OutboxDispatcher MUST use the legacy
    /// GetPendingAsync/MarkAsDispatchedAsync flow even though a coordinator is registered.</summary>
    bool DueTimeDispatchActive { get; }

    /// <summary>Atomically claims up to batchSize due, unblocked rows for the given owner,
    /// applying due-time eligibility, session ordering, and session-head predicates.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(Guid claimId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Dispatch-start fence: conditionally sets the first DispatchStartedAtUtc,
    /// extends the owner's lease, and returns the SQL-computed lease deadline (UTC).
    /// Returns null when the fence is lost (cancelled, stale owner, or expired lease).</summary>
    Task<DateTime?> TryStartDispatchAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>Owned checkpoint: marks the row dispatched iff the caller still owns it.
    /// Returns false (no-op) for a stale owner.</summary>
    Task<bool> TryCompleteAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>Releases an owned, not-yet-started claim so the row is immediately
    /// reclaimable. A stale owner's release affects zero rows.</summary>
    Task ReleaseClaimAsync(string outboxMessageId, Guid claimId, CancellationToken cancellationToken = default);
}
~~~

Dispatcher selection is pinned by the capability signal, never by registration presence: `OutboxDispatcher` gains a constructor overload taking an optional `IOutboxDispatchCoordinator` (both existing constructors preserved; `AddNimBusOutboxDispatcher` in `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:312` passes `sp.GetService<IOutboxDispatchCoordinator>()`) and runs the claim/fence/checkpoint protocol iff `coordinator is { DueTimeDispatchActive: true }`. No coordinator (custom `IOutbox` providers) or `DueTimeDispatchActive == false` (default mode) runs today's `GetPendingAsync`/`MarkAsDispatchedAsync` flow unchanged. Unit tests pin all three branches, including that the inactive branches never invoke the claim API and the active branch never invokes `GetPendingAsync`.

SqlServerOutbox implements both companions; `DueTimeDispatchActive` returns `ScheduledDelivery == SqlOwnedDueTime` from its options. Registration is unconditional — the same singleton is TryAddSingleton-registered for the companion interfaces in src/NimBus.Outbox.SqlServer/ServiceCollectionExtensions.cs while preserving the existing IOutbox/IOutboxCleanup/IOutboxMetricsQuery registrations — so composition does not depend on when configuration is evaluated. That registration path (and the SqlServerOutbox constructor) also validates the options invariants (revision-5 finding 5): `SendLeaseDuration` positive and at most 24 hours, `TimeSpan.Zero <= SendLeaseSafetyMargin < SendLeaseDuration`, throwing `ArgumentOutOfRangeException` naming the offending property, alongside the existing eager `ConnectionString` check.

Extend the inline, idempotent EnsureTableExistsAsync DDL in src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs; this project does not use DbUp migration scripts. **The entire DDL batch runs under a session-scoped exclusive application lock** (`sp_getapplock` with resource `'NimBus.Outbox:' + schema + '.' + table`, bounded @LockTimeout, return code checked, `sp_releaseapplock` in finally), and every guard is evaluated inside the lock, making concurrent rolling-startup initialization safe. Add:

- OutboxSequenceNumber BIGINT IDENTITY(1,1) NOT NULL plus a unique index. Existing rows receive opaque values; immediate rows also have values but are not cancellable.
- StoredAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME() — SQL-assigned insert timestamp, the ordering authority for unscheduled rows in `SqlOwnedDueTime` mode only. Migration backfills existing rows from CreatedAtUtc (best available approximation) in the same applock-serialized batch; the OutboxSequenceNumber tiebreak keeps ordering deterministic regardless.
- CancelledAtUtc DATETIME2 NULL.
- DispatchStartedAtUtc DATETIME2 NULL.
- DispatchClaimId UNIQUEIDENTIFIER NULL.
- DispatchClaimedUntilUtc DATETIME2 NULL.
- EffectiveDueAtUtc AS COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc) PERSISTED — the ordering key as a real column, so the session predicates are sargable and key-range lockable (revision-5 finding 3).
- A session-ordering nonclustered index on (SessionId, EffectiveDueAtUtc, OutboxSequenceNumber) INCLUDE (DispatchedAtUtc, CancelledAtUtc, DispatchStartedAtUtc, DispatchClaimId, DispatchClaimedUntilUtc) — the index the HOLDLOCK session subqueries seek through; its key order defines the ranges whose locks cover a backdated row's insertion point.
- A dispatchable index covering DispatchedAtUtc, CancelledAtUtc, ScheduledEnqueueTimeUtc, StoredAtUtc, OutboxSequenceNumber, SessionId, and claim expiry, filtered to terminal-null rows where supported.

Existing pending rows remain pending, existing dispatched rows remain terminal, and existing scheduled rows remain dispatchable (eagerly in default mode; under the due-time rule after cutover). The migration may rewrite the table when adding IDENTITY; document the deployment lock/maintenance implication. The additive columns are ignored by pre-upgrade binaries, and default-mode reads never consult them for selection or ordering, which is what makes phase 1 of the cutover safe. Keep the existing validated schema/table identifiers and parameterize every timeout ID, handle, owner, timestamp interval, and batch value.

StoreScheduledAsync uses INSERT ... OUTPUT INSERTED.OutboxSequenceNumber on both standalone and ambient connection/transaction paths. Cancellation uses sequence plus TimeoutId plus ScheduledEnqueueTimeUtc IS NOT NULL so a forged/mistyped handle cannot affect another or nonscheduled row.

**Pending-row selection is mode-split (revision-4 finding 5):**

- `BrokerScheduleAtDispatch`: the legacy query, selection and ordering verbatim — `SELECT TOP (@BatchSize) … WHERE [DispatchedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL ORDER BY [CreatedAtUtc] ASC` (the CancelledAtUtc guard is vacuously true for anything default mode can produce; it exists solely to make a misconfigured downgrade unable to dispatch a cancelled row). No claims, no due filter, no StoredAtUtc/sequence ordering.
- `SqlOwnedDueTime`: claiming is a single UPDATE ... OUTPUT over a TOP batch — one autocommit statement, so every lock it takes is held until that statement's transaction commits. Row-candidate locking uses UPDLOCK, READPAST, and ROWLOCK; READPAST is valid here even under READ_COMMITTED_SNAPSHOT precisely because UPDLOCK explicitly requests locking semantics (revision-5 finding 3(i)). **Both session subqueries carry HOLDLOCK and no READPAST**: HOLDLOCK forces locking serializable key-range semantics even when RCSI is on, the ranges are taken through the session-ordering index (SessionId, EffectiveDueAtUtc, OutboxSequenceNumber) and cover the insertion point of any earlier-keyed row, and never skipping locked blockers means an in-flight head is always seen. A backdated INSERT racing the claim either blocks on the range lock until the claim commits (and is then excluded by the committed head columns) or commits first (and is then seen by the subqueries) — at most one live head in either order; claim deadlocks (error 1205) are retried by the dispatcher as an ordinary empty round. Eligibility is:
  - DispatchedAtUtc IS NULL.
  - CancelledAtUtc IS NULL.
  - EffectiveDueAtUtc <= SYSUTCDATETIME() for scheduled rows; a null ScheduledEnqueueTimeUtc is immediately eligible by definition (no CreatedAtUtc comparison anywhere).
  - No live claim, or an expired claim.
  - For a non-null SessionId, **both** of (invariant 10): (a) **only when the candidate has not dispatch-started** (`c.[DispatchStartedAtUtc] IS NULL`; revision-5 finding 2) — no earlier non-terminal due row exists for that session in any claim state, where "earlier" is EffectiveDueAtUtc ASC, OutboxSequenceNumber ASC; a dispatch-started candidate (the expired head being reclaimed) bypasses (a) and is governed by lease expiry plus (b) alone; and (b) **no other row of that session is in flight** — `NOT EXISTS (SELECT 1 FROM <table> h WITH (HOLDLOCK) WHERE h.[SessionId] = c.[SessionId] AND h.[Id] <> c.[Id] AND h.[DispatchedAtUtc] IS NULL AND h.[CancelledAtUtc] IS NULL AND (h.[DispatchStartedAtUtc] IS NOT NULL OR (h.[DispatchClaimId] IS NOT NULL AND h.[DispatchClaimedUntilUtc] > SYSUTCDATETIME())))` — the durable session-head predicate. Excluding the candidate by Id is what allows the expired head itself to be reclaimed while everything else in the session stays blocked.

Claim ordering uses the same SQL-owned composite key. Claim at most one active row per session per round; sessionless rows remain independently claimable. Update OutboxDispatcherHostedService to poll again immediately after any successful dispatch, rather than only after a full batch, so one busy session is not throttled to one message per polling interval.

Immediately before network I/O, TryStartDispatchAsync conditionally sets the first DispatchStartedAtUtc — and, in the same UPDATE, extends DispatchClaimedUntilUtc to SYSUTCDATETIME() + SendLeaseDuration and **OUTPUTs the new deadline** — only when the caller still owns an unexpired claim and cancellation is null. The dispatcher runs the subsequent Send under a linked CancellationToken budgeted to the SQL-returned deadline minus SendLeaseSafetyMargin. This bound is best effort (invariant 9): if the send outlives the lease anyway, the row becomes reclaimable and a concurrent duplicate attempt is permitted and absorbed; the stale owner can never checkpoint. Cancellation sets CancelledAtUtc only while DispatchStartedAtUtc is null; it may win even after reservation. If cancellation wins, start affects zero rows and the dispatcher skips send. If start wins, cancel reports TooLate.

For a due scheduled row OutboxDispatcher calls ISender.Send with zero delay, then checkpoints DispatchedAtUtc conditionally on claim ownership. On bounded-send timeout or failure it releases or lets the claim expire but retains DispatchStartedAtUtc, because the broker outcome can be ambiguous and cancellation can no longer truthfully claim prevention; the row remains the session head (invariant 10b) — its session's other rows, including any backdated arrivals, stay blocked behind it until an owned retry of that same row succeeds. Cancellation/shutdown compensation retains the bounded non-cancelled checkpoint behavior already present on origin/master. A send accepted before a crash can be retried after lease expiry; the duplicate has the same MessageId and is absorbed by the application inbox/state guard.

**Metrics queries are mode-split (revision-4 finding 4).** IOutboxMetricsQuery's implementation:

- `SqlOwnedDueTime`: pending count and `GetOldestPendingEnqueuedAtUtcAsync` cover **dispatch-eligible** rows only (`DispatchedAtUtc IS NULL AND CancelledAtUtc IS NULL AND (ScheduledEnqueueTimeUtc IS NULL OR ScheduledEnqueueTimeUtc <= SYSUTCDATETIME())`), and the oldest-pending baseline is `MIN([EffectiveDueAtUtc])` (the persisted `COALESCE(ScheduledEnqueueTimeUtc, StoredAtUtc)` column) — dispatch lag for a scheduled row counts from its due time, so a month-ahead timeout contributes zero lag until due and ≈0 immediately after. Not-yet-due rows appear in neither pending nor lag (a separate scheduled-backlog count may be added later; out of scope).
- `BrokerScheduleAtDispatch`: today's queries unchanged (oldest `CreatedAtUtc` over all `DispatchedAtUtc IS NULL` rows) — future rows are immediately actionable in this mode, so their storage age is genuine lag. The gauge derivation in NimBusGaugeBackgroundService is untouched; only the provider query differs by mode.

Update PurgeDispatchedAsync to purge both dispatched rows by DispatchedAtUtc and cancelled terminal rows by CancelledAtUtc, retaining the existing API name for compatibility and documenting the widened terminal cleanup behavior.

## Timeout identity, receive marker, and telemetry/audit semantics

Add backward-compatible default scheduled-message metadata to IMessage/Message and its wire mapping:

- ScheduledMessageId — the logical TimeoutId. Distinct from the per-attempt transport MessageId; equal to it only on the first delivery.
- ScheduledEnqueueTimeUtc — the original due time.

Update src/NimBus.Core/Messages/UserPropertyName.cs (enum members), src/NimBus.Core/Messages/Models/Message.cs, src/NimBus.ServiceBus/MessageHelper.cs (both `Create` and `CreateDeferredMessage`), and src/NimBus.ServiceBus/MessageContext.cs so native and CloudEvents messages carry the marker and IMessageContext exposes both values (as default interface members so custom contexts and MessageContextStub stay compatible). **Preserve the marker — and, for marked messages, the workflow CorrelationId — through every clone path:**

- `ResponseService.CreateRetryResponse` copies ScheduledMessageId/ScheduledEnqueueTimeUtc from the inbound IMessageContext onto the clone, and **when the marker is present sets `CorrelationId = messageContext.CorrelationId` instead of `messageContext.MessageId`** (revision-4 finding 2; ResponseService.cs:181 today). Unmarked messages keep today's byte-identical behavior. `SendToDeferredSubscription` copies the marker; it already preserves CorrelationId (ResponseService.cs:227).
- `MessageHelper.CreateDeferredMessage` stamps the marker properties like `Create` does, so parked timeouts keep identity through the deferred subscription.
- `DeferredMessageProcessor`'s republish path preserves the marker properties (verify while implementing; add a round-trip test either way).
- `MessageContext.ScheduleRedelivery` already copies all application properties wholesale and already mints a fresh transport MessageId — covered by construction, pinned with a regression test.
- **Resolver-bound responses and operator resubmission (revision-5 finding 1):** `ResponseService.CreateResponse` (ResponseService.cs:132) copies ScheduledMessageId/ScheduledEnqueueTimeUtc onto every Resolver-bound response (ErrorResponse, ResolutionResponse, SkipResponse, DeferralResponse, dead-letter, PendingHandoff), and for marked messages stamps the response-only `WorkflowCorrelationId = messageContext.CorrelationId`; the response's own CorrelationId keeps the `= MessageId` audit-linkage convention untouched. `MessageEntity` and `UnresolvedEvent` (NimBus.MessageStore.Abstractions) gain nullable `ScheduledMessageId`, `ScheduledEnqueueTimeUtc`, `WorkflowCorrelationId`; `ResolverService.CreateMessageEntity`/`CreateUnresolvedEvent` project them; Cosmos documents are additive and the SQL message store adds the columns via a new DbUp script plus `StoreMessage`/`MapMessageRow` updates. `ManagerClient.Resubmit` (ManagerClient.cs:66; the WebApp resubmit paths delegate to it) and the CLI's `Container.Resubmit` (Container.cs:185) stamp the marker pair onto the ResubmissionRequest and, for marked entities, set its CorrelationId from `WorkflowCorrelationId` (falling back to the entity CorrelationId for pre-upgrade rows). Unmarked entities keep today's construction byte-identical. A resubmitted timeout therefore reaches `HandleResubmissionRequest` with its logical identity and workflow conversation ID intact, and invariant 12's guard decides Fired vs IgnoredLate.
- `SendContinuationRequestToSelf`/`SendProcessDeferredRequest` construct new control messages, not timeout redeliveries; they intentionally do not carry the marker (documented).

**Expose the identity to typed handlers (revision-4 finding 1):** `IEventHandlerContext.ScheduledMessageId`/`ScheduledEnqueueTimeUtc` default interface members; `EventHandlerContext` backs them from its wrapped `IMessageContext`; `EventJsonHandler<T>` populates them alongside MessageId/CorrelationId in its existing initializer (EventJsonHandler.cs:46–55).

Legacy messages without the marker remain ordinary events. MessageType stays EventRequest and EventId remains topology-assigned.

The platform can observe schedule, cancel, dispatch, receive, and failure, but cannot infer whether application state accepted or ignored the timeout. Add a neutral diagnostic signal, separate from HandlerOutcome:

~~~csharp
public enum ScheduledMessageHandlingOutcome
{
    Fired = 0,
    IgnoredLate = 1,
}

void ReportScheduledMessageOutcome(ScheduledMessageHandlingOutcome outcome);
~~~

Expose this on IEventHandlerContext with a backward-compatible default and back it through EventHandlerContext/IMessageContext. A timeout handler calls it only after its durable compare-and-set/no-op decision. If it is not called, NimBus records Received only and does not invent a Fired result. Do not change ResolutionResponse, Resolver status, or operator audit types.

Use existing Publisher, Outbox, and Consumer meters/sources:

- Add nimbus.message.schedule.operations on NimBus.Publisher for operation schedule/cancel and bounded outcomes scheduled, cancellation_requested, cancelled_before_dispatch, already_cancelled, too_late, not_found, unsupported, failed.
- Add nimbus.message.timeout.operations on NimBus.Consumer for received, explicitly reported fired, ignored_late, and failed. Timeout telemetry keys on the ScheduledMessageId marker, so retried timeouts still count as timeout traffic.
- Keep nimbus.outbox.enqueued and nimbus.outbox.dispatched; add schedule mode/outcome to existing spans/logs and use dispatched/failed for the due-time send. Pending/lag gauges are mode-scoped as defined above.
- Add bounded attributes nimbus.schedule.operation and nimbus.schedule.mode. Metric dimensions are limited to messaging.system, destination, event type, operation, mode, and outcome. TimeoutId, MessageId, SessionId, and CorrelationId are span/structured-log fields only, never metric tags. Never log the message body or secrets.
- Direct schedule remains a publish producer span with operation name schedule; cancellation uses the Publisher source with settle/cancel_scheduled semantics. Outbox dispatch remains the existing linked producer span.
- Persisted outbox timestamps are the transport audit. Resolver continues to show the received timeout as an ordinary message. Application workflow state remains the durable business audit.

## Step-by-step implementation and files

1. **Pin the contract with failing tests first.**
   - Add tests/NimBus.SDK.Tests/PublisherClientSchedulingTests.cs for interface accessibility, TimeoutId stamped as first-delivery MessageId AND ScheduledMessageId marker, AF-118 identity/lineage, native/CloudEvents parity, CancellationToken propagation, handle kind, validation, default-interface compatibility, and legacy concrete bridges (including: legacy outbox CancelScheduled(long) still throws NotSupported; direct cancel accepts a shape-valid handle without pair verification).
   - Extend tests/NimBus.SDK.Tests/EventJsonHandlerTests.cs: a context carrying the marker yields an IEventHandlerContext exposing ScheduledMessageId/ScheduledEnqueueTimeUtc; an unmarked context yields nulls; a custom IEventHandlerContext implementation without the new members still compiles and returns the defaults.
   - Extend tests/NimBus.Core.Tests/ResponseServiceTests.cs: CreateRetryResponse and SendToDeferredSubscription preserve ScheduledMessageId/ScheduledEnqueueTimeUtc and do NOT reuse the inbound transport MessageId; for a **marked** context CreateRetryResponse preserves the inbound CorrelationId (first and second retry), for an **unmarked** context it keeps CorrelationId = messageContext.MessageId byte-identically.
   - Add direct delegation/cancellation tests to tests/NimBus.ServiceBus.Tests/SenderTests.cs and extend tests/NimBus.ServiceBus.Tests/ServiceBusTestDoubles.cs with scheduled/cancelled sequence capture and gated tasks.
   - Update tests/NimBus.Core.Tests/OutboxTests.cs to first demonstrate the current 0/NotSupported behavior and unsafe cancel/dispatch race, then replace those assertions with the approved contract (mode-gated).
   - Add dispatcher-selection pinning tests (tests/NimBus.Core.Tests): no coordinator → legacy GetPendingAsync flow; coordinator with `DueTimeDispatchActive == false` → legacy flow and the claim API is never called; coordinator active → claim protocol and GetPendingAsync is never called.
   - Add SqlServerOutboxOptions validation tests: margin equal to / greater than / one tick below the duration, negative margin, non-positive and above-24h duration — each rejected with ArgumentOutOfRangeException naming the property.

2. **Add the additive core scheduling contract.**
   - Add src/NimBus.Core/Messages/ScheduledMessageHandle.cs and ScheduledMessageCancellationOutcome.cs.
   - Update src/NimBus.Core/Messages/ISender.cs with default handle overloads and XML documentation while preserving existing methods; the XML docs state the per-mode validation scope (pair validation SQL-only) and outcome semantics.
   - Update src/NimBus.Core/Messages/Models/Message.cs and src/NimBus.Core/Messages/UserPropertyName.cs with optional ScheduledMessageId/ScheduledEnqueueTimeUtc metadata plus the response-only WorkflowCorrelationId property; update src/NimBus.Core/Messages/IMessageContext.cs with backward-compatible default members exposing all three.
   - Update src/NimBus.Core/Messages/ResponseService.cs so CreateRetryResponse and SendToDeferredSubscription carry the marker, CreateRetryResponse preserves CorrelationId for marked messages only, and CreateResponse copies the marker onto every Resolver-bound response and stamps WorkflowCorrelationId for marked messages (revision-5 finding 1).
   - Validate nonblank/128-character TimeoutId, positive sequence, and defined handle kind at the public boundary.

3. **Expose the workflow API through the SDK after AF-118 is merged.**
   - Update src/NimBus.SDK/IPublisherClient.cs and PublisherClient.cs with the exact signatures above.
   - Update src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs: add the ScheduledMessageId/ScheduledEnqueueTimeUtc default members and implement them on EventHandlerContext (settable, defaulting from the wrapped IMessageContext); update src/NimBus.SDK/EventHandlers/EventJsonHandler.cs to populate them.
   - Reuse AF-118's context-aware message construction rather than adding a second lineage implementation; set CloudEvent id and native MessageId to TimeoutId and stamp ScheduledMessageId.
   - Keep and mark the old concrete long overloads as Obsolete bridges; keep the obsolete ServiceBusClient constructor behavior identical.
   - Update src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs only as needed to preserve the existing endpoint-bound direct/outbox sender selection.

4. **Round-trip the timeout marker through transports and every clone path.**
   - Update src/NimBus.ServiceBus/MessageHelper.cs (Create AND CreateDeferredMessage) and MessageContext.cs (parse + expose on IMessageContext); add absent-legacy and present-round-trip tests in tests/NimBus.ServiceBus.Tests/MessageHelperTests.cs and MessageContextTests.cs, plus a ScheduleRedelivery regression test (marker survives, fresh transport MessageId minted) and a DeferredMessageProcessor republish round-trip test.
   - Update src/NimBus.ServiceBus/Sender.cs so the richer direct schedule returns BrokerSequenceNumber and richer cancellation validates kind and shape (only) before delegating to the sequence-only broker API.
   - Update src/NimBus.Testing/InMemoryMessageBus.cs and tests/NimBus.EndToEnd.Tests/Infrastructure/InMemoryBus.cs only enough to preserve handle/marker/cancellation semantics; do not introduce a general virtual-time scheduler. Add end-to-end tests: a timeout that throws once, retries via RetryRequest, and still fires with its original ScheduledMessageId **and** the workflow CorrelationId visible on IEventHandlerContext; and marker/correlation access after a deferred park + republish.

5. **Add optional outbox scheduling and coordination capabilities.**
   - Add src/NimBus.Core/Outbox/IScheduledOutbox.cs and IOutboxDispatchCoordinator.cs with the exact signatures in the schema/protocol section, including `DueTimeDispatchActive` (start fence returns the SQL lease deadline).
   - Extend src/NimBus.Core/Outbox/OutboxMessage.cs with OutboxSequenceNumber, StoredAtUtc, CancelledAtUtc, DispatchStartedAtUtc, DispatchClaimId, and DispatchClaimedUntilUtc.
   - Update src/NimBus.Core/Outbox/OutboxSender.cs to use IScheduledOutbox for the handle-based schedule/cancel path, keep the legacy long-only path exactly as today (0 in default mode / NotSupportedException for cancel in all modes; provider sequence returned by legacy Schedule only under `SqlOwnedDueTime`), retain trace capture, and avoid storing anything before capability/mode validation.
   - Update OutboxDispatcher.cs (new optional-coordinator constructor overload; protocol selected iff `coordinator is { DueTimeDispatchActive: true }`, pinned by the step-1 tests) and OutboxDispatcherHostedService.cs with mode-gated due-only claims, per-session ordering including the earlier-predecessor rule (first claims only — a dispatch-started candidate bypasses it) AND the session-head rule, start fence with lease extension and deadline-budgeted bounded send token, owned checkpoint/release, and zero-delay Send. `AddNimBusOutboxDispatcher` resolves the optional coordinator via `sp.GetService<IOutboxDispatchCoordinator>()`. The legacy (default-mode / no-coordinator) path keeps today's GetPendingAsync flow untouched.

6. **Implement the applock-serialized idempotent SQL migration and mode-split conditional commands.**
   - Update src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs: wrap EnsureTableExistsAsync in sp_getapplock/sp_releaseapplock (session owner, bounded timeout, checked return code); guarded DDL incl. StoredAtUtc with CreatedAtUtc backfill, the persisted `EffectiveDueAtUtc` computed column, and the session-ordering index; StoreScheduledAsync OUTPUT; ambient transactional cancellation; claim/start(+lease-extend, OUTPUT deadline)/complete/release commands; **both** session predicates with HOLDLOCK and no READPAST (earlier-predecessor scoped to not-yet-started candidates, and session-head), UPDLOCK+READPAST+ROWLOCK on the candidate scan only, and 1205-deadlock retry treated as an empty claim round; the mode-split pending selection (legacy CreatedAtUtc query verbatim + CancelledAtUtc guard in default mode; claim protocol in SqlOwnedDueTime); mode-split metrics queries (EffectiveDueAtUtc over eligible rows in SqlOwnedDueTime; today's CreatedAtUtc query in default mode); terminal cleanup; and mode gating with `DueTimeDispatchActive` derived from options.
   - Update src/NimBus.Outbox.SqlServer/SqlServerOutboxOptions.cs with `ScheduledDelivery` (default `BrokerScheduleAtDispatch`), `SendLeaseDuration`, and `SendLeaseSafetyMargin`, validated per the invariants above (positive bounded duration; 0 <= margin < duration).
   - Update src/NimBus.Outbox.SqlServer/ServiceCollectionExtensions.cs to register the companion interfaces against the same singleton and enforce the options invariants eagerly next to the existing ConnectionString check.
   - Preserve current SQL identifier validation; all data remains parameterized.

7. **Add explicit, bounded telemetry without changing business audit.**
   - Update src/NimBus.Core/Diagnostics/NimBusMeters.cs, MessagingAttributes.cs, and NimBusConsumerInstrumentation.cs.
   - Update src/NimBus.OpenTelemetry/Instrumentation/InstrumentingSenderDecorator.cs to forward/instrument rich schedule and cancel operations without double-counting ordinary publish/outbox metrics.
   - Update src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs and the backing IMessageContext/EventHandlerContext for ReportScheduledMessageOutcome, without adding a new HandlerOutcome or Resolver state.
   - Extend tests/NimBus.OpenTelemetry.Tests/TelemetryCoverageTests.cs, OutboxInstrumentationTests.cs, and GaugeBackgroundServiceTests.cs; assert the new instruments, the mode-scoped lag baselines, and that no high-cardinality identifier appears on any metric.

8. **Preserve timeout identity through the Resolver audit chain and operator resubmission (revision-5 finding 1).**
   - Extend src/NimBus.MessageStore.Abstractions/MessageEntity.cs and States/UnresolvedEvent.cs with nullable `ScheduledMessageId`, `ScheduledEnqueueTimeUtc`, and `WorkflowCorrelationId`.
   - Update src/NimBus.Resolver/Services/ResolverService.cs (`CreateMessageEntity`, `CreateUnresolvedEvent`) to project the three fields from the received response.
   - Update src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs (`StoreMessage`, `MapMessageRow`, search projections) and add a new DbUp script under Schema/ with the three nullable columns; Cosmos and the in-memory conformance store are additive.
   - Update src/NimBus.Manager/ManagerClient.cs `Resubmit` and src/NimBus.CommandLine/Container.cs `Resubmit` to stamp the marker pair on the ResubmissionRequest and set `CorrelationId = WorkflowCorrelationId ?? entity.CorrelationId` for marked entities; unmarked entities keep today's construction byte-identical. The WebApp resubmit paths delegate to IManagerClient and need no change beyond the entity carrying the fields.
   - Tests: MessageTrackingStore conformance round-trip of the new fields across all three providers; ResolverService projection unit tests; ManagerClient/CLI construction tests (marked vs unmarked); and the end-to-end terminal-failure→resubmit test — a timeout fails terminally, the operator resubmits from the store, and the handler context exposes the original ScheduledMessageId and workflow CorrelationId (stale case no-ops as IgnoredLate).

9. **Extend provider and integration coverage.**
   - Add an optional scheduled-outbox conformance fixture under src/NimBus.Testing/Conformance rather than changing required IOutbox conformance.
   - Add tests/NimBus.Outbox.SqlServer.Tests/SqlServerOutboxSchedulingIntegrationTests.cs and update SqlServerOutboxBatchInsertCommandTests.cs only where the insert shape changes.
   - Cover fresh schema, migration from the old table shape (including the `EffectiveDueAtUtc` computed column and session-ordering index), running EnsureTableExistsAsync twice sequentially AND N-way concurrently (applock serialization), legacy pending/dispatched rows, **default-mode behavioral parity with today including ordering** (out-of-order CreatedAtUtc rows dispatch in CreatedAtUtc order, matching an old-binary-shaped reader), ambient commit/rollback for schedule and cancel, identity uniqueness, due/future eligibility, clock-skew/equal/regressed CreatedAtUtc rows, two-worker disjoint claims, the reserved-predecessor two-worker barrier, **backdated-insert-after-reserve and backdated-insert-after-start barriers (session head blocks the earlier-key row), expired-started-head reclamation with a backdated earlier-key row present (the head bypasses ordering, is the only reclaimable row, and the session never wedges; all other session rows stay blocked), backdated-goes-next ordering after the head terminalizes, the insertion-boundary claim race (a backdated INSERT racing the claim statement never yields two live heads, executed with READ_COMMITTED_SNAPSHOT both OFF and ON via ALTER DATABASE in the fixture)**, lease expiry, start-fence lease extension with returned deadline, a sender that ignores cancellation (stale checkpoint affects zero rows; exactly one terminalization), stale-owner fences, started-row session blocking, cancellation/start races, version-skew simulation (a legacy-shaped `WHERE DispatchedAtUtc IS NULL` reader against new-style rows, proving why the mode gate exists), **mode-scoped pending/lag gauges (month-ahead due row ≈0 lag in SqlOwnedDueTime; not-yet-due row invisible to both gauges; default-mode gauge parity)**, and cleanup. Use NIMBUS_SQL_TEST_CONNECTION and keep tests inconclusive when it is absent, matching the existing fixture.

10. **Document the contract and remove stale guidance.**
   - Update docs/orchestration.md with the authoritative timeout record shape, logical-vs-transport identity (ScheduledMessageId vs per-attempt MessageId), CorrelationId preservation on timeout retries, the handler-context surface (`IEventHandlerContext.ScheduledMessageId`), both diagrams, direct reconciliation gaps, SQL-owned due-time latency/availability tradeoff, the phased delivery-mode cutover and its mixed-fleet semantics (including the downgrade misconfiguration), the best-effort lease bound and permitted duplicate overlap, the session-head rule and backdated-row semantics (including the started-head ordering bypass), operator resubmission of failed timeouts (identity preserved through the audit chain; the guard decides Fired vs IgnoredLate), the claim locking strategy and its RCSI compatibility, cancellation outcomes per mode (including direct's shape-only validation), handler guard keyed on ScheduledMessageId, observability ownership (mode-scoped lag), and race matrix.
   - Update docs/sdk-api-reference.md with IPublisherClient signatures, the new IEventHandlerContext members, handle persistence, default/legacy behavior (legacy outbox cancellation stays unsupported; migration is the handle API), validation scope per mode, and direct versus outbox guarantees.
   - Update docs/building-adapters.md with ambient schedule/cancel transactions, dispatcher lease and bounded-send behavior, the concurrent-safe startup migration, cutover runbook, retention, and operational guidance.
   - Narrowly update docs/adr/009-orchestration-via-application-services.md and docs/adr/005-transactional-outbox-sql-server.md; preserve ADR-009's no-saga boundary and state that cancellation is optimization only.
   - Do not create a Resolver workflow projection, WebApp saga state, generic scheduler, or Cosmos/SQL atomicity claim.

## Race-focused test matrix

| Scenario | Direct expectation | SQL-outbox expectation | Required proof |
| --- | --- | --- | --- |
| State/schedule transaction rolls back | Not atomic; reconciliation contract documented | State and scheduled row both absent | Ambient rollback integration test |
| Schedule succeeds | Broker handle returned | Non-zero local handle returned in same transaction (`SqlOwnedDueTime`) | SDK/sender/SQL tests |
| Handle API used in default mode | N/A | InvalidOperationException naming required mode; nothing stored | Mode-gate unit test |
| Legacy CancelScheduled(long), outbox mode | N/A (direct unchanged) | NotSupportedException in all modes; Unsupported via rich mapping | Bridge unit test |
| Mismatched TimeoutId in handle | Shape-only validation; the given sequence is cancelled (documented, best effort) | NotFound; zero rows affected | Direct doc-behavior test + SQL CAS test |
| Timeout handler throws → RetryRequest clone | Marker + ScheduledMessageId preserved; new transport MessageId; **CorrelationId preserved (workflow conversation ID)** | Same | ResponseService + e2e retry test (first and second retry) |
| Unmarked message → RetryRequest clone | CorrelationId = parent MessageId, byte-identical to today | Same | ResponseService legacy-parity test |
| Typed handler reads timeout identity | `IEventHandlerContext.ScheduledMessageId` == TimeoutId after first delivery, retry, and deferred replay | Same | EventJsonHandler unit tests + e2e |
| Timeout parked to deferred subscription and republished | Marker preserved through park + republish | Same | CreateDeferredMessage/DeferredMessageProcessor round-trip tests |
| Throttle redelivery (ScheduleRedelivery) | Marker survives wholesale property copy; fresh transport MessageId | Same | Regression test |
| Duplicate detection vs retry | Retry not dropped (distinct transport MessageId) | Same | Test asserting retry MessageId differs from original |
| Future timeout | Broker owns due time | Not claimable before SQL UTC due time | Future/due SQL test |
| Immediate row with skewed/equal/regressed CreatedAtUtc | N/A | Immediately eligible (null schedule); ordering by StoredAtUtc + sequence, deterministic (`SqlOwnedDueTime`) | Clock-skew tests |
| Default-mode selection and ordering | N/A | Bit-for-bit today's behavior: CreatedAtUtc ASC selection identical to an old binary, even with out-of-order CreatedAtUtc vs insertion order | Default-mode parity test incl. ordering |
| Cancel before dispatch | Broker request returns CancellationRequested, still best effort | CancelledBeforeDispatch; row can never pass start fence | Direct fake + SQL integration |
| Cancel after reservation, before start | Broker-specific race | Cancellation still wins; start affects zero rows | Gated race test |
| Cancel versus dispatch start | Activation may still win | Exactly one conditional UPDATE wins | Two-connection barrier test |
| Cancel after start/dispatched | CancellationRequested may race with activation | TooLate; no false cancelled claim | Unit/integration tests |
| Duplicate cancel | Repeats best-effort request or propagates broker failure | AlreadyCancelled with no second mutation | Idempotency tests |
| Unknown/rolled-back/purged handle | Broker failure is surfaced | NotFound, no row affected | Validation/store tests |
| Dispatcher crash before start | N/A | Lease expires; row is reclaimable and remains cancellable until start | Lease test |
| Crash/failure after start before send result | N/A | Cancellation remains closed; retry permitted; session successors stay blocked | Coordinator test |
| Worker A reserved row N, paused; worker B claims same session | N/A | B cannot claim N+1: live reservation blocks successors; no READPAST on the predicates | Gated two-worker barrier test at reserve boundary |
| **Backdated row inserted while session head is Reserved** | N/A | Earlier-key row not claimable by any worker until the head terminalizes; then it dispatches before later successors | Gated backdated-after-reserve barrier + ordering test |
| **Backdated row inserted while session head is DispatchStarted** | N/A | Same: head blocks regardless of lease state; only the head row may be retried/reclaimed | Gated backdated-after-start barrier test |
| **Expired head reclamation (incl. backdated earlier-key row present)** | N/A | Started row with expired lease bypasses the ordering predicate and is the only reclaimable row; backdated and successor rows stay blocked until it terminalizes — the session never wedges | Gated expired-head reclamation test with a backdated row inserted before reclaim |
| **Backdated INSERT races the claim statement** | N/A | HOLDLOCK range locks on the session-ordering index serialize insert vs claim; never two live heads, in either commit order | Gated insertion-boundary barrier test, RCSI OFF and ON |
| Sender ignores cancellation; send outlives lease | N/A | Row reclaimed; overlapping duplicate attempt permitted and absorbed; stale owner's checkpoint affects zero rows; exactly one terminalization | Gated cancellation-ignoring-sender test |
| Second worker vs in-flight same-session row | N/A | Successor row not claimable while predecessor is non-terminal (reserved OR started) | Gated two-worker FIFO test |
| Ambiguous send then retry | Direct sequence-save reconciliation may duplicate | Duplicate of row N may arrive after N+1; absorbed by idempotency (invariant 10 qualification) | Gated dispatcher test + handler idempotency test |
| Broker accepts send, checkpoint crashes | Direct sequence-save reconciliation may duplicate | Row replays after lease; same MessageId may duplicate | Gated dispatcher test |
| Two live dispatchers | Broker API independent | Distinct owned rows; at most one active row per session | Concurrent SQL test |
| Concurrent EnsureTableExistsAsync on rolling startup | N/A | All initializers succeed; applock serializes DDL; schema correct | Concurrent-initialization test |
| Mixed-version fleet (phase 1, default mode) | Unchanged | Old and new binaries behave identically to today, including row selection order; additive columns ignored | Default-mode parity + version-skew simulation test |
| Old dispatcher vs new-style rows (misused cutover) | N/A | Documented failure mode motivating the mode gate: legacy reader would claim/eagerly schedule and bypass CancelledAtUtc | Version-skew simulation test (legacy-shaped query) |
| **Month-ahead timeout and lag gauges** | N/A | Contributes no pending/lag before due; ≈0 lag at due time (`SqlOwnedDueTime`); default-mode gauges unchanged from today | Mode-scoped gauge tests |
| Future earlier row plus immediate same-session row | Broker handles activation separately | Future row does not block eligible immediate row | Ordering test |
| Completion commits just before timeout | Cancel requested after state commit; delivery may occur | Cancel wins if pre-start, otherwise delivery occurs | Handler sees terminal state and no-ops |
| Timeout wins just before completion | Timeout CAS reaches Fired | Same | Completion reloads/version-conflicts; one terminal path |
| Duplicate timeout delivery | Same ScheduledMessageId may redeliver | Same | Inbox/processed-ID guard keyed on ScheduledMessageId emits one effect |
| Old timeout after reschedule | TimeoutId/generation mismatch | Same | IgnoredLate, no outgoing side effect |
| Timeout handler throws terminally | Normal retry/dead-letter behavior | Same | Failed metric/span; state not falsely Fired |
| Terminally failed timeout resubmitted by operator | Handler context exposes original ScheduledMessageId + workflow CorrelationId after resubmission; stale timeout no-ops IgnoredLate | Same | ResponseService/Resolver/store/Manager round-trip tests + terminal-failure→resubmit e2e test |
| Unmarked message resubmitted by operator | ResubmissionRequest construction byte-identical to today | Same | ManagerClient/CLI legacy-parity test |
| Dispatcher composition (no coordinator / inactive / active) | N/A | Legacy flow, legacy flow (claim API never called), claim protocol (GetPendingAsync never called) | Three pinned selection unit tests |
| SendLeaseSafetyMargin >= SendLeaseDuration (or negative / non-positive duration) | N/A | Startup rejection: ArgumentOutOfRangeException naming the property; no silent zero send window | Options validation unit tests |
| Handler ignores stale timeout | Operational Resolver status remains Completed | Same | Explicit IgnoredLate diagnostic; no business effect |
| Existing pre-migration rows | N/A | Pending rows dispatch, dispatched rows remain terminal; StoredAtUtc backfilled from CreatedAtUtc | Migration compatibility test |
| Cancelled-row retention/metrics | N/A | Excluded from due backlog; eventually purged as terminal | Gauge/cleanup tests |

## Verification required for AF-120

Run from the implementation worktree root, in this order:

1. dotnet restore src/NimBus.sln
2. dotnet build src/NimBus.sln --no-restore --configuration Release
3. dotnet test src/NimBus.sln --no-build --configuration Release --verbosity normal
4. Run the SQL scheduling integration suite with NIMBUS_SQL_TEST_CONNECTION set so migration, concurrent-initialization, race, lease, barrier, session-head/backdated, expired-head-with-backdated-row, insertion-boundary (READ_COMMITTED_SNAPSHOT OFF and ON), clock-skew, gauge, version-skew, and resubmission round-trip tests execute rather than report Inconclusive.
5. Run documentation link/content checks used by AF-116 and git diff --check.

No implementation should be considered complete if the release analyzers, full suite, or SQL race/migration/version-skew tests fail.
