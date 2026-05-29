# Feature Specification: Inbox Pattern — Idempotent Consumers

Feature Branch: `012-inbox-idempotent-consumers`
Created: 2026-05-29
Updated: 2026-05-29
Status: Proposed
Input: User description: "The transactional outbox guarantees at-least-once delivery from the publisher side, but duplicates still occur (Service Bus redelivery on transient handler failure, Resubmit replay, network retries). Without an inbox, handlers must each be idempotent — hard to enforce. The inbox stores MessageId of every successfully processed message in a dedup store; subsequent deliveries of the same ID are skipped. Paired with the outbox → exactly-once processing semantics. Proposed API: `AddNimBusSubscriber(\"billing\", b => { b.AddHandler<OrderPlaced, OrderPlacedHandler>(); b.UseInbox(opts => { opts.DeduplicationStore = InboxStore.Cosmos; opts.RetentionPeriod = TimeSpan.FromDays(7); }); });` Abstraction: `interface IInboxStore { Task<bool> TryRecordAsync(string messageId, CancellationToken ct); Task PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct); }`."

## Problem

NimBus's transactional outbox (`NimBus.Outbox.SqlServer`, decorating `ISender` via `OutboxSender` per `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`) guarantees **at-least-once** delivery on the publish side: the row is written in the caller's transaction and `OutboxDispatcherHostedService` later publishes it. "At-least-once" is the operative phrase — the same logical message reaches a subscriber more than once whenever:

- Azure Service Bus redelivers after a transient handler failure (the subscriber abandoned the lock; `StrictMessageHandler.HandleEventRequest` re-throws on `TransientException` so the broker re-presents the message).
- An operator hits **Resubmit** in the WebApp, which republishes an event that may already have been processed once.
- A network retry on the dispatcher side double-publishes a row that was in fact delivered before the `MarkAsDispatchedAsync` ack landed.

Today the only defence is for **every handler to be idempotent by hand**. That is hard to enforce, easy to get wrong, and invisible in code review — there is no platform-level guarantee and no audit signal when a duplicate slips through. NimBus has a clean place to add the missing half of the exactly-once story: the message pipeline (`IMessagePipelineBehavior`, wrapped around handler dispatch in `MessageHandler.Handle` at `src/NimBus.Core/Messages/MessageHandler.cs`).

This spec adds a **consumer-side inbox**: a deduplication store keyed on the message's `MessageId` (exposed by `IMessageContext.MessageId`). A new pipeline behavior **checks** whether a `MessageId` has already been *successfully processed* and short-circuits if so; otherwise it dispatches the handler and **records the id only after the handler succeeds**. Paired with the outbox, this closes the loop to **effectively exactly-once processing for redeliveries**.

### The record-timing decision (record-on-success, not record-before-dispatch)

The single most important correctness decision in this spec is **when** the inbox records a `MessageId`. There are two options:

- **Record-before-dispatch** (record on first sight, then run the handler): a single atomic "record-if-absent" call decides whether to run the handler. *This is wrong for NimBus.* If the handler then fails transiently (the dominant reason a message is redelivered in the first place), the broker redelivers — but the id was already recorded, so the inbox skips it and **the message is never reprocessed**. That silently converts at-least-once delivery into **at-most-once processing**: a transiently-failed message is lost. An inbox whose job is "make consumers reliable" must never *cause* message loss.
- **Record-on-success** (check first, run the handler, record only after it completes): a transiently-failed handler leaves **no** inbox record, so the broker's redelivery reprocesses it normally. The id is recorded only once the handler has actually succeeded; every later redelivery of that id is then skipped. This preserves at-least-once and adds dedup on top — i.e. *effectively exactly-once* for the redelivery case, never at-most-once.

This spec adopts **record-on-success**. The residual it trades for is benign: two *genuinely concurrent first deliveries* of the same id (competing receivers, before either has recorded) could both pass the pre-dispatch check and both run the handler — a rare double-process, the same exposure a hand-rolled idempotent handler already tolerates, and one Service Bus largely avoids for locked/sessioned messages. Losing a transiently-failed message is the far worse failure mode, so the design optimises against it. True single-transaction exactly-once (handler side effects + inbox record in one commit) remains out of scope (see Out of Scope).

## Scope

In scope:
- A new abstraction `IInboxStore` in `src/NimBus.Core/Inbox/IInboxStore.cs` with `HasProcessedAsync(string messageId, …)` (read — has this id already been recorded as processed), `RecordProcessedAsync(string messageId, …)` (idempotent record, called only after a successful handler), and `PurgeExpiredAsync(DateTimeOffset olderThan, …)`. (The issue's single `TryRecordAsync` conflates the read and the write into one before-dispatch call; splitting them is what enables record-on-success — see the record-timing decision above and Resolved Questions.)
- A new pipeline behavior `InboxMiddleware` (`src/NimBus.Core/Inbox/InboxMiddleware.cs`) implementing `IMessagePipelineBehavior`. Because behaviors wrap the terminal handler (`MessageHandler.Handle` → `_pipeline.Execute(ctx, (c,t) => HandleByMessageType(c,t))`), the behavior runs **before handler dispatch**, short-circuits duplicates by not calling `next`, and records the id **after** `next` returns successfully.
- A Cosmos implementation `CosmosInboxStore` in `src/NimBus.MessageStore.CosmosDb/` (the issue says `src/NimBus.MessageStore/CosmosInboxStore.cs`; the real project is **`NimBus.MessageStore.CosmosDb`** — corrected here).
- A SQL implementation as a **new package `src/NimBus.Inbox.SqlServer/`** mirroring the `NimBus.Outbox.SqlServer` layout precisely: a `SqlServerInboxOptions`, a `SqlServerInbox` that owns the inline `EnsureTableExistsAsync` schema bootstrap, and a `ServiceCollectionExtensions.AddNimBusSqlServerInbox(...)`.
- An in-memory `IInboxStore` for tests in `NimBus.Testing` (parallels `InMemoryMessageStore`), plus conformance test coverage.
- An opt-in registration on the subscriber builder (`NimBusSubscriberBuilder`) that adds `InboxMiddleware` to the pipeline and binds the chosen `IInboxStore` (see Open Questions for the exact API shape — the issue's `b.UseInbox(...)` does not exist on the current builder).
- A background purge that calls `IInboxStore.PurgeExpiredAsync(...)` on a timer (a `BackgroundService`, since no outbox-cleanup hosted service exists today to reuse — see Assumptions).
- A `DuplicateDetected` lifecycle signal emitted when a duplicate is skipped, routed to observers through the existing `MessageLifecycleNotifier` / `IMessageLifecycleObserver` mechanism so the Resolver records it.
- A `Skipped`-family `resolutionStatus` representation in the WebApp so operators see "skipped (duplicate)" (the `ResolutionStatus` enum already has a `Skipped` member; this reuses it with a duplicate-distinguishing reason — see FR-060).
- Documentation in `docs/inbox-pattern.md`.

Out of scope:
- Making the inbox the default. It is opt-in; an un-configured subscriber behaves exactly as today (no perf cost, no behavior change).
- Single-transaction exactly-once spanning the handler's own database transaction and the inbox record. The inbox record is a separate round-trip; this delivers *effectively* exactly-once (no message loss; deduplicated redeliveries), not a distributed-transaction guarantee across the handler's side effects (see the record-timing decision and Open Questions).
- Eliminating the concurrent-first-delivery double-process window. Record-on-success accepts that two simultaneous first deliveries of the same id may both run; removing it would require a pre-dispatch lock/claim that reintroduces the message-loss risk this design rejects.
- Content-hash deduplication ("same id, different content"). v1 keys on `MessageId` only; payload-hash is an Open Question.
- Per-session inbox semantics. The inbox is a per-message check independent of session ordering (sessions are already FIFO via `SessionState`; dedup is orthogonal).
- An inbox provider for any store other than Cosmos, SQL Server, and in-memory.

## User Scenarios & Testing

### User Story 1 - First delivery of a message is processed and recorded after success (Priority: P1)

As an adapter author, I want the first delivery of a given `MessageId` to run my handler normally and — only once the handler has succeeded — be recorded in the inbox, so that any later redelivery of the same id is recognised as a duplicate without risking the loss of a message whose handler failed.

Why this priority: This is the baseline the whole feature rests on, and the record-*after*-success ordering is what makes it safe.

Independent Test: Configure a subscriber with the inbox enabled and an in-memory `IInboxStore`. Deliver one message with `MessageId = "m1"`. Assert the handler ran once and, after it returned, the store contains `"m1"`.

Acceptance Scenarios:

1. Given an inbox-enabled subscriber and a fresh inbox store, When a message with `MessageId = "m1"` is delivered, Then `IInboxStore.HasProcessedAsync("m1", …)` returns `false`, the pipeline calls `next` (handler dispatch), the handler executes once, and **after** `next` returns successfully `RecordProcessedAsync("m1", …)` is called.
2. Given the handler completes successfully, When the pipeline returns, Then the store retains `"m1"` with a creation timestamp so it can later be purged by TTL.
3. Given a subscriber **without** the inbox configured, When any message is delivered, Then `InboxMiddleware` is not in the pipeline and behavior is identical to today (no store call, no overhead).

---

### User Story 2 - Duplicate delivery is skipped before the handler runs (Priority: P1)

As an operator, I want a redelivered message (Service Bus redelivery, Resubmit replay, network retry) **whose original delivery already succeeded** to be skipped without re-running the handler, so that side effects are not duplicated.

Why this priority: Skipping already-processed duplicates is the feature's entire purpose. Without it, the inbox records but never protects.

Independent Test: Deliver `MessageId = "m1"` once (handler succeeds, id recorded), then deliver `"m1"` again. Assert the handler ran exactly once and the second delivery was completed (settled) without invoking the handler.

Acceptance Scenarios:

1. Given `"m1"` has already been recorded (its first delivery succeeded), When `"m1"` is delivered again, Then `HasProcessedAsync("m1", …)` returns `true`, `InboxMiddleware` does **not** call `next`, and the handler is never invoked for the second delivery.
2. Given the duplicate is skipped, When the pipeline returns, Then the message is settled/completed (not abandoned) so the broker does not redeliver it endlessly.
3. Given the duplicate is skipped, When the Resolver is queried, Then a `DuplicateDetected` lifecycle signal was emitted for `"m1"` via `MessageLifecycleNotifier`, carrying the `MessageId`, `EventId`, `EndpointId`, and `SessionId` from the `MessageLifecycleContext`.
4. Given a Resubmit of an already-processed event (same `MessageId`), When it is redelivered, Then it is skipped exactly as a broker redelivery would be (the inbox does not distinguish the *source* of the duplicate, only the id).

---

### User Story 3 - A transiently-failed message is reprocessed, not lost (Priority: P1)

As an adapter author, I want a message whose handler threw a transient error to be reprocessed on the broker's redelivery — NOT skipped as a duplicate — so the inbox never converts a recoverable failure into a lost message.

Why this priority: This is the failure mode that record-before-dispatch would get catastrophically wrong. It is the reason the spec records on success.

Independent Test: Deliver `MessageId = "m1"` with a handler that throws on the first delivery and succeeds on the second. Assert the handler ran twice, the message was processed exactly once successfully, and `"m1"` is recorded only after the successful run.

Acceptance Scenarios:

1. Given the handler throws a `TransientException` on first delivery, When the pipeline runs, Then `RecordProcessedAsync("m1", …)` is **not** called (the record only commits after `next` returns), the exception propagates, and `MessageHandler` abandons the message.
2. Given the broker redelivers `"m1"`, When `HasProcessedAsync("m1", …)` is checked, Then it returns `false` (no record was committed for the failed run), so the handler runs again.
3. Given the second delivery succeeds, When the pipeline returns, Then `"m1"` is recorded and any third delivery is skipped.

---

### User Story 4 - Expired records allow legitimate reprocessing (Priority: P2)

As an operator, I want inbox records to expire after a configurable retention period, so the dedup store does not grow unbounded and a deliberately re-sent message after the retention window is processed again rather than wrongly skipped.

Why this priority: Without TTL the store grows forever; with too aggressive a TTL, late redeliveries reprocess. The retention period is the knob that balances these.

Independent Test: Record `"m1"` with `RetentionPeriod = TimeSpan.FromDays(7)`. Advance the clock past 7 days, run `PurgeExpiredAsync`. Re-deliver `"m1"`; assert the handler runs again.

Acceptance Scenarios:

1. Given a record for `"m1"` older than `RetentionPeriod`, When the background purge runs `PurgeExpiredAsync(now - RetentionPeriod, …)`, Then `"m1"` is removed from the store.
2. Given `"m1"` was purged, When `"m1"` is delivered again, Then `HasProcessedAsync` returns `false`, the handler runs, and on success `"m1"` is recorded afresh.
3. Given a record for `"m1"` *within* the retention window, When the purge runs, Then `"m1"` is retained and a subsequent delivery is still skipped.

---

### User Story 5 - Inbox-store failure does not silently drop messages (Priority: P1)

As an operator, I want a transient inbox-store failure (Cosmos throttle, SQL timeout) to NOT cause a message to be silently lost or skipped in a way the platform cannot recover from.

Why this priority: A dedup store sitting in front of every handler must fail safe. The wrong failure mode (swallow + skip) drops the message; the right failure mode lets the broker redeliver.

Independent Test: Inject a faulting `IInboxStore` whose `HasProcessedAsync` throws. Deliver a message. Assert the exception surfaces as a transient failure so the broker redelivers, rather than the handler being skipped.

Acceptance Scenarios:

1. Given `HasProcessedAsync` throws a transient store error, When a message is delivered, Then `InboxMiddleware` lets the exception propagate (it does not treat a failed check as "already seen"), so the existing `MessageHandler` catch path abandons the message and the broker redelivers.
2. Given the handler succeeded but `RecordProcessedAsync` then throws, When the exception propagates, Then `MessageHandler` abandons and the broker redelivers; the handler runs again on redelivery (a duplicate side effect — at-least-once). This is the accepted failure mode: a record write that fails after a successful handler yields one extra processing, never message loss. (Documented in `docs/inbox-pattern.md`.)
3. Given the purge job throws, When it runs on its timer, Then the failure is logged and the job continues on the next tick (a failed purge never crashes the host), mirroring `OutboxDispatcherHostedService`'s catch-log-continue loop.

---

### User Story 6 - WebApp surfaces "skipped (duplicate)" (Priority: P3)

As an operator reviewing an endpoint's events, I want a duplicate that the inbox skipped to show a distinct "skipped (duplicate)" status, so I can tell intentional skips from dedup skips.

Why this priority: Observability polish. The dedup protection works without it, but operators benefit from seeing why a message did not run.

Independent Test: Trigger a duplicate skip end-to-end against the in-memory store, then read the event via the WebApp event API and assert its `resolutionStatus` reflects a duplicate skip.

Acceptance Scenarios:

1. Given a duplicate was skipped, When the event is read via the WebApp event API, Then its `resolutionStatus` is `Skipped` with a duplicate-distinguishing reason (FR-060) so the UI can render "skipped (duplicate)".
2. Given the event-details page renders that event, When the status badge is shown, Then it is visually distinguishable from an operator-initiated Skip.

## Edge Cases

- **Missing or empty `MessageId`.** Service Bus normally sets `MessageId`; NimBus reads it via `IMessageContext.MessageId`. If it is null/empty, the inbox cannot deduplicate — `InboxMiddleware` MUST fall through to the handler (process the message) and log a warning, never throw. Deduplication is best-effort when the key is absent.
- **Concurrent first delivery of the same id** (two competing receivers, neither has recorded yet). Both `HasProcessedAsync` checks can return `false`, so **both may run the handler** — the accepted residual of record-on-success (see the record-timing decision). `RecordProcessedAsync` MUST be **idempotent** (insert-or-ignore / upsert) so the second record does not error. This is a rare double-process, strictly less harmful than the message loss that a pre-dispatch atomic claim would risk. Service Bus session locks and per-message locks make true simultaneity uncommon; documented as a known limitation.
- **Handler throws *after* doing partial side effects.** Because the record only commits on a clean return from `next`, a handler that throws leaves no inbox record and is redelivered. If the handler is not itself idempotent for its partial side effects, the redelivery may repeat them — but that is the pre-existing at-least-once reality the inbox does not worsen (it never *suppresses* a redelivery of a non-recorded id). Documented.
- **Resubmit reuses the same `MessageId`.** Per the issue, Resubmit replays with the original id, so the inbox skips it (its first processing already recorded the id) — which may surprise an operator who *wanted* the reprocess. Documented behaviour: Resubmit-after-retention works; Resubmit-within-retention is skipped. Flagged in Open Questions (whether Resubmit should mint a new id).
- **Session-ordered endpoints.** The inbox check is per message, independent of session. A skipped duplicate inside a session MUST still be settled so it does not block the session head (a duplicate must not park the session the way a `SessionBlockedException` does).
- **Purge against an empty / brand-new store.** `PurgeExpiredAsync` MUST be a no-op returning cleanly when nothing matches.
- **Inbox configured but no `IInboxStore` registered.** The builder MUST fail fast at startup with a clear message (mirroring `AddNimBusOutboxDispatcher`'s "OutboxDispatcherSender is not registered" guard), not silently disable dedup.
- **Very large id.** SQL column is bounded (`NVARCHAR(512)` matching the outbox's `MessageId` column); ids longer than the column MUST be rejected or hashed deterministically rather than truncated (truncation could collide distinct ids). v1 rejects with a logged warning and falls through.

## Requirements

### Functional Requirements

#### Abstraction

- FR-001: A new interface `IInboxStore` MUST be added at `src/NimBus.Core/Inbox/IInboxStore.cs` exposing:
  - `Task<bool> HasProcessedAsync(string messageId, CancellationToken cancellationToken = default);` — returns `true` if `messageId` was already recorded as successfully processed, `false` otherwise. A read; MUST NOT mutate the store.
  - `Task RecordProcessedAsync(string messageId, CancellationToken cancellationToken = default);` — records `messageId` as processed. MUST be **idempotent**: recording an id that is already present MUST succeed silently (insert-or-ignore / upsert), so the concurrent-first-delivery race (two handlers both succeeding) does not surface an error.
  - `Task<int> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);` — deletes records created before `olderThan`; returns the number purged. (The issue's signature returns `Task`; returning the count mirrors `IOutboxCleanup.PurgeDispatchedAsync`'s `Task<int>` and lets the purge job log throughput.)
  - The interface deliberately **splits the read (`HasProcessedAsync`) from the write (`RecordProcessedAsync`)** rather than exposing the issue's single before-dispatch `TryRecordAsync`. This split is what allows recording only after the handler succeeds (the record-timing decision). The implementation MUST NOT collapse them back into a single record-on-first-sight call.
- FR-002: `IInboxStore` MUST live in `NimBus.Core` (not a provider package) so `InboxMiddleware` depends only on the abstraction, exactly as `OutboxSender` depends on `IOutbox` in `NimBus.Core.Outbox`.

#### Pipeline behavior

- FR-010: A new `InboxMiddleware : IMessagePipelineBehavior` MUST be added at `src/NimBus.Core/Inbox/InboxMiddleware.cs`. Its `Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct)` MUST, in order:
  1. Read `context.MessageId`.
  2. If null/empty, log a warning and call `next` (fall through — see Edge Cases).
  3. `if (await _inboxStore.HasProcessedAsync(messageId, ct))` → **skip**: do not call `next`, emit the `DuplicateDetected` lifecycle signal (FR-040), and return so the outer `MessageHandler` settles the message normally.
  4. Otherwise `await next(context, ct)` (dispatch the handler).
  5. **Only after `next` returns successfully**, `await _inboxStore.RecordProcessedAsync(messageId, ct)`. If `next` throws, the record MUST NOT be written — the exception propagates so the message is abandoned and the broker redelivers it for reprocessing (User Story 3).
- FR-011: `InboxMiddleware` MUST run **before handler dispatch**. This is guaranteed by the pipeline architecture: `MessageHandler.Handle` invokes `_pipeline.Execute(messageContext, (ctx, ct) => HandleByMessageType(ctx, ct), …)` (`src/NimBus.Core/Messages/MessageHandler.cs`), so every behavior wraps `HandleByMessageType` (the handler dispatch). When `InboxMiddleware` is the first registered behavior it is the outermost wrapper, so it sees the handler's completion/exception and can record on success / withhold on failure.
- FR-012: `InboxMiddleware` MUST NOT swallow exceptions from `HasProcessedAsync` or `RecordProcessedAsync`. A store failure MUST propagate so the message is abandoned and redelivered, never silently skipped (User Story 5).
- FR-013: When a duplicate is skipped, `InboxMiddleware` MUST NOT call `Abandon`/throw — the message MUST be settled (completed) so the broker does not redeliver indefinitely.
- FR-014: `RecordProcessedAsync` MUST be awaited *inside* the behavior's `Handle` (before it returns to `MessageHandler`), so that a record-write failure is observable as described in User Story 5 scenario 2. It MUST NOT be fire-and-forget (that would silently lose records and reintroduce the duplicate-processing it exists to prevent).

#### Registration

- FR-020: The subscriber MUST gain an opt-in inbox registration. The issue proposes `b.UseInbox(opts => { opts.DeduplicationStore = InboxStore.Cosmos; opts.RetentionPeriod = …; })`; the current `NimBusSubscriberBuilder` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`) has **no** `UseInbox` method and does not register pipeline behaviors (behaviors are registered on `INimBusBuilder.AddPipelineBehavior<T>()` per `src/NimBus.Core/Extensions/NimBusBuilder.cs`). The implementation MUST reconcile to the real shape — a `NimBusSubscriberBuilder.UseInbox(Action<InboxOptions>)` that (a) records `InboxMiddleware` into the `PipelineBehaviorRegistry` and (b) registers the selected `IInboxStore` — and the chosen approach MUST be noted in Open Questions until ratified.
- FR-021: `InboxOptions` MUST expose at least `RetentionPeriod` (`TimeSpan`, default 7 days) and a store selector. The `DeduplicationStore` enum (`InboxStore.Cosmos` / `InboxStore.SqlServer` / `InboxStore.InMemory`) selects which registered `IInboxStore` is used; selecting a store whose provider package is not referenced MUST fail fast at startup.
- FR-022: If `UseInbox` is configured but no matching `IInboxStore` is registered in DI, startup MUST throw with a clear message (mirroring the `OutboxDispatcherSender is not registered` guard in `AddNimBusOutboxDispatcher`).
- FR-023: A subscriber that does NOT call `UseInbox` MUST behave identically to today — `InboxMiddleware` is absent from the pipeline and `HasBehaviors` reflects whatever other behaviors exist.

#### SQL Server provider (new package)

- FR-030: A new package `src/NimBus.Inbox.SqlServer/` MUST be created mirroring `NimBus.Outbox.SqlServer` exactly: a `SqlServerInboxOptions` (ConnectionString, Schema, TableName, FullTableName, AutoCreateTable), a `SqlServerInbox : IInboxStore`, and a `ServiceCollectionExtensions` with `AddNimBusSqlServerInbox(string connectionString)` / `AddNimBusSqlServerInbox(Action<SqlServerInboxOptions>)`.
- FR-031: `SqlServerInbox` MUST own an inline, idempotent `EnsureTableExistsAsync` exactly like `SqlServerOutbox.EnsureTableExistsAsync` (the outbox does **not** use DbUp numbered scripts — the issue's "DbUp numbered SQL scripts" describes `NimBus.MessageStore.SqlServer`, not the outbox; the inbox MUST mirror the *outbox*). The table MUST have at minimum `[MessageId] NVARCHAR(512) NOT NULL PRIMARY KEY` and `[CreatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()`, with the same `ValidateSqlIdentifier` guard for schema/table names.
- FR-032: `SqlServerInbox.HasProcessedAsync` MUST be a `SELECT 1 … WHERE [MessageId] = @id` existence check. `SqlServerInbox.RecordProcessedAsync` MUST be an **idempotent insert** — `INSERT … WHERE NOT EXISTS`, `MERGE`, or an `INSERT` whose primary-key violation is caught and treated as success — so a concurrent double-record does not throw.
- FR-033: `SqlServerInbox.PurgeExpiredAsync` MUST `DELETE … WHERE [CreatedAtUtc] < @Cutoff` and return the affected-row count, mirroring `SqlServerOutbox.PurgeDispatchedAsync`.
- FR-034: `AddNimBusSqlServerInbox` MUST register the implementation as a singleton via `TryAddSingleton<IInboxStore>` (matching the outbox's `TryAddSingleton<IOutbox>`).

#### Cosmos provider

- FR-035: A `CosmosInboxStore : IInboxStore` MUST be added to `src/NimBus.MessageStore.CosmosDb/` (corrected from the issue's `src/NimBus.MessageStore/`). It MUST store one document per `MessageId` (the id as the document id / partition key). `HasProcessedAsync` MUST be a point read (`ReadItemAsync` / a cheap existence query) returning whether the document exists. `RecordProcessedAsync` MUST `UpsertItemAsync` (or `CreateItemAsync` treating a 409 Conflict as success), so a concurrent double-record is idempotent. `PurgeExpiredAsync` deletes documents older than the cutoff. A registration extension MUST be added alongside the existing `CosmosDbMessageStoreBuilderExtensions`.
- FR-036: The Cosmos document MAY carry a TTL so the store auto-expires records; if container TTL is used, `PurgeExpiredAsync` becomes a defensive backstop rather than the primary cleanup. The chosen approach MUST be documented.

#### In-memory provider

- FR-037: An in-memory `IInboxStore` MUST be added to `NimBus.Testing` (parallel to `InMemoryMessageStore`), backed by a thread-safe map keyed on `MessageId` with a recorded timestamp. `HasProcessedAsync` is a `ContainsKey`; `RecordProcessedAsync` is an idempotent `TryAdd`/indexer set (a repeat add is a no-op, not an error); `PurgeExpiredAsync` removes entries older than the cutoff. It MUST be registerable for `InboxStore.InMemory` and is the default store used by unit/integration tests.

#### Lifecycle / Resolver integration

- FR-040: When a duplicate is skipped, a `DuplicateDetected` signal MUST be emitted through the existing lifecycle mechanism. Because `IMessageLifecycleObserver` (`src/NimBus.Core/Extensions/IMessageLifecycleObserver.cs`) has a fixed method set (`OnMessageReceived`, `OnMessageCompleted`, `OnMessageFailed`, `OnMessageDeadLettered`), the implementation MUST either (a) add an `OnDuplicateDetected(MessageLifecycleContext, CancellationToken)` method with a default no-op (consistent with the existing default-method pattern) and a `MessageLifecycleNotifier.NotifyDuplicateDetected(...)`, or (b) reuse `OnMessageCompleted` carrying a duplicate marker on the context. Option (a) is preferred for an explicit signal; the choice is recorded in Open Questions.
- FR-041: The Resolver (`src/NimBus.Resolver/Services/ResolverService.cs`) MUST map the `DuplicateDetected` signal to a persisted event outcome of `ResolutionStatus.Skipped` (existing member in `src/NimBus.MessageStore.Abstractions/States/ResolutionStatus.cs`) with a reason that identifies it as a duplicate (FR-060), so the audit trail records the skip.

#### WebApp status

- FR-060: A duplicate skip MUST be representable in the WebApp's event model. The `ResolutionStatus` enum already exposes `Skipped`; the duplicate distinction MUST be carried in a reason/sub-status field rather than a new enum member, so existing `resolutionStatus`-based filters (`src/NimBus.WebApp/.../events-panel.tsx`, the `ResolutionStatus` enum in `api-spec.yaml` / `ApiContract.g.cs`) continue to function unchanged.
- FR-061: The event-details surface (`src/NimBus.WebApp/ClientApp/src/pages/event-details.tsx`, `components/event-details/message-listing.tsx`) MUST render the duplicate skip distinctly (e.g. "skipped (duplicate)") using the reason field from FR-060, without regressing existing Skipped rendering.

#### Background purge

- FR-050: A background purge MUST call `IInboxStore.PurgeExpiredAsync(DateTimeOffset.UtcNow - RetentionPeriod, …)` on a timer. Since there is **no existing outbox-cleanup hosted service** to reuse (the outbox exposes `IOutboxCleanup.PurgeDispatchedAsync` but no `BackgroundService` calls it — confirmed: only `SqlServerOutbox` and `IOutboxCleanup` reference it), a new `InboxPurgeHostedService : BackgroundService` MUST be added in `NimBus.SDK/Hosting`, modelled on `OutboxDispatcherHostedService`'s polling loop with catch-log-continue semantics.
- FR-051: The purge interval MUST be configurable via `InboxOptions` (default: hourly). The hosted service MUST be registered by the `UseInbox` opt-in, not auto-registered for subscribers that do not use the inbox.

#### Documentation & tests

- FR-070: `docs/inbox-pattern.md` MUST document: the at-least-once → effectively-exactly-once rationale, the **record-on-success** decision and the message-loss failure mode it avoids (with the concurrent-first-delivery residual it accepts), the `UseInbox` registration, the three providers, the retention/purge model, the `MessageId`-as-key assumption (and the Resubmit interaction), and the `DuplicateDetected` → `Skipped` mapping.
- FR-071: Unit tests MUST cover: (1) first delivery runs the handler and records the id after success; (2) duplicate delivery (already recorded) is skipped without invoking the handler; (3) **transiently-failed first delivery records nothing and is reprocessed on redelivery** (User Story 3); (4) an expired (purged) record allows reprocessing; (5) a faulting store propagates (does not skip); (6) `RecordProcessedAsync` is idempotent under a simulated concurrent double-record; (7) missing `MessageId` falls through to the handler.
- FR-072: An integration/end-to-end test MUST demonstrate effectively-exactly-once with outbox + inbox together: publish through the outbox, deliver twice (simulated redelivery) where the first delivery succeeds, assert the handler ran once and the second was skipped with a `DuplicateDetected` signal; plus a variant where the first delivery throws and the redelivery succeeds, asserting the handler ran twice and the message was not lost — added under `tests/NimBus.EndToEnd.Tests` (alongside `PipelineAndLifecycleTests`).
- FR-073: The storage conformance suite (`src/NimBus.Testing/Conformance/`) MUST gain `IInboxStore` cases verifying: `HasProcessedAsync` is `false` before and `true` after `RecordProcessedAsync`; `RecordProcessedAsync` is idempotent (a repeat call does not throw and does not create a second row); and `PurgeExpiredAsync` removes aged rows — across the SQL, Cosmos, and in-memory providers (mirroring the existing per-provider conformance runs).

### Non-Functional Requirements

- NFR-001: The inbox adds **two** store round-trips to a first delivery (one `HasProcessedAsync` read before dispatch, one `RecordProcessedAsync` write after a successful handler) and **one** to a skipped duplicate (the `HasProcessedAsync` read only). This is the deliberate cost of record-on-success; an implementation MUST NOT collapse to a single before-dispatch write to save a round-trip, because that reintroduces the message-loss failure mode (see the record-timing decision). The read SHOULD be a cheap point lookup (PK / point read).
- NFR-002: The inbox MUST add zero overhead to subscribers that do not opt in (no behavior in the pipeline, no DI resolution).
- NFR-003: `InboxMiddleware` MUST be safe for concurrent invocation across competing receivers. Correctness for the redelivery case depends on `RecordProcessedAsync` idempotency; the concurrent-first-delivery double-process window is an accepted residual (Edge Cases), not a correctness violation, since it never loses or wrongly skips a message.
- NFR-004: The SQL inbox table MUST have its primary key on `MessageId` so existence checks and idempotent inserts are index-backed; the purge predicate on `CreatedAtUtc` SHOULD be index-supported for large tables.
- NFR-005: No new NuGet dependencies in `NimBus.Core` (the abstraction and middleware use only existing types). `NimBus.Inbox.SqlServer` MAY reference `Microsoft.Data.SqlClient` (same version as `NimBus.Outbox.SqlServer`, 6.0.1).
- NFR-006: A purge failure MUST never crash the host; it logs and retries next tick (matches `OutboxDispatcherHostedService`).
- NFR-007: The feature MUST NOT change the public behavior or signatures of existing pipeline behaviors, lifecycle observers, or the outbox.

## Key Entities

- **`IInboxStore`** — new abstraction in `NimBus.Core.Inbox`. `HasProcessedAsync` (read) + `RecordProcessedAsync` (idempotent write, called after handler success) + `PurgeExpiredAsync`. The read/write split is what enables record-on-success. Mirrors `IOutbox` / `IOutboxCleanup` in placement and DI lifetime.
- **`InboxMiddleware`** — new `IMessagePipelineBehavior` in `NimBus.Core.Inbox`. Runs before handler dispatch (wraps `HandleByMessageType`); checks-then-skips a known duplicate, or dispatches and records on success.
- **`InboxOptions` / `InboxStore` enum** — registration options surfaced via `NimBusSubscriberBuilder.UseInbox`. `RetentionPeriod`, purge interval, store selector.
- **`SqlServerInbox` / `SqlServerInboxOptions`** — new `NimBus.Inbox.SqlServer` package, mirroring `NimBus.Outbox.SqlServer` (inline `EnsureTableExistsAsync`, PK on `MessageId`, `TryAddSingleton<IInboxStore>`, idempotent insert).
- **`CosmosInboxStore`** — new type in `NimBus.MessageStore.CosmosDb` (corrected project name). Document-per-`MessageId`, point-read existence, upsert/409-as-success record, optional container TTL.
- **In-memory `IInboxStore`** — in `NimBus.Testing`, concurrent-map backed; default test store.
- **`InboxPurgeHostedService`** — new `BackgroundService` in `NimBus.SDK/Hosting`, modelled on `OutboxDispatcherHostedService`.
- **`DuplicateDetected` lifecycle signal** — new observer hook (or carried marker) routed via `MessageLifecycleNotifier` to `IMessageLifecycleObserver`, mapped by the Resolver to `ResolutionStatus.Skipped` with a duplicate reason.

## Success Criteria

### Measurable Outcomes

- SC-001: With the inbox enabled, delivering the same `MessageId` N times **where the first delivery succeeds** invokes the handler exactly once. Verified by integration test against the in-memory store and the SQL conformance run.
- SC-002: A first delivery whose handler throws transiently is reprocessed on redelivery (the handler runs again and the message is not lost) — i.e. the inbox never produces at-most-once. Verified by the failing-then-succeeding test (FR-071.3 / FR-072 variant).
- SC-003: A duplicate skip emits exactly one `DuplicateDetected` lifecycle signal and persists a `Skipped`-with-duplicate-reason event outcome via the Resolver. Verified by end-to-end test.
- SC-004: After a record exceeds `RetentionPeriod` and the purge runs, the same `MessageId` is processed again. Verified by a clock-advancing unit test.
- SC-005: A faulting `IInboxStore` causes the message to be abandoned/redelivered (not silently skipped). Verified by injecting a throwing store and asserting the abandon path runs.
- SC-006: A subscriber without `UseInbox` shows no change in pipeline composition or behavior. Verified by an assertion that `InboxMiddleware` is absent from the behavior set.
- SC-007: `HasProcessedAsync`, `RecordProcessedAsync` (including idempotency), and `PurgeExpiredAsync` pass the conformance suite identically across SQL, Cosmos, and in-memory providers (FR-073).
- SC-008: The WebApp renders a duplicate skip as "skipped (duplicate)", distinct from an operator Skip, with no regression to existing `resolutionStatus` filters.

## Assumptions

- `IMessageContext.MessageId` is the dedup key and is populated for normal Service Bus deliveries (confirmed: `new string MessageId { get; }` on `src/NimBus.Core/Messages/IMessageContext.cs`).
- Pipeline behaviors run before handler dispatch via `MessageHandler.Handle` → `_pipeline.Execute(ctx, (c,t) => HandleByMessageType(c,t))` (confirmed at `src/NimBus.Core/Messages/MessageHandler.cs`). Registering `InboxMiddleware` first makes it the outermost wrapper, so it observes the handler's success/failure and can record-on-success.
- The outbox is the publish-side half of the pair and uses an **inline** `EnsureTableExistsAsync` (not DbUp). The inbox SQL package mirrors the outbox, so DbUp is intentionally not used here (DbUp numbered scripts are the `NimBus.MessageStore.SqlServer` pattern, e.g. `Schema/0001_Schema.sql … 0012_*.sql`).
- No outbox-cleanup `BackgroundService` exists to reuse; `IOutboxCleanup.PurgeDispatchedAsync` is referenced only by `SqlServerOutbox`. The inbox introduces its own purge host modelled on `OutboxDispatcherHostedService`.
- `ResolutionStatus.Skipped` already exists and is the right outcome for a deduplicated message; the duplicate distinction is a reason, not a new enum member (avoids breaking `resolutionStatus` string filters across the WebApp and CLI).
- The lifecycle-observer mechanism (`MessageLifecycleNotifier` + `IMessageLifecycleObserver` with default no-op methods) is the right channel for the `DuplicateDetected` signal, consistent with how `005-lifecycle-queue-time` and `006-blocked-by-event-link` surface lifecycle data to the Resolver.
- Abandoning a message on an un-recorded transient failure causes the broker to redeliver it (the established `MessageHandler` `catch (TransientException) → Abandon` path), which is what makes record-on-success safe.

## Out of Scope

- Making the inbox on by default. Opt-in only.
- Single-transaction exactly-once spanning the handler's own side effects. This is effectively-once (separate inbox round-trips, record-on-success), not a distributed-transaction guarantee.
- Eliminating the concurrent-first-delivery double-process window (would need a pre-dispatch claim that reintroduces message-loss risk).
- Content-hash / payload-hash deduplication for "same id, different content".
- Per-session or per-correlation dedup semantics beyond the per-`MessageId` check.
- Inbox providers beyond SQL Server, Cosmos, and in-memory.
- Changing Resubmit to mint a new `MessageId` (called out as an Open Question, not implemented here).

## Open Questions

- **Required vs opt-in.** Confirmed opt-in for v1 (per the issue and the perf-cost concern). Default-on is explicitly out of scope.
- **Is `MessageId` always trustworthy?** Service Bus sets `MessageId` from the publisher; Resubmit reuses the same id. So within the retention window, a Resubmit of an already-processed event is *skipped*. Is that desired, or should Resubmit mint a fresh id (publisher-side change) so operators can deliberately reprocess? Needs a product decision.
- **`DuplicateDetected` signal shape.** Add an `OnDuplicateDetected` observer method (preferred, explicit) vs reuse `OnMessageCompleted` with a marker (no interface change). The interface uses default no-op methods, so adding a method is non-breaking — but it touches every observer's contract surface.
- **`UseInbox` API placement.** The issue's `b.UseInbox(...)` lives on the subscriber builder, but pipeline behaviors today register via `INimBusBuilder.AddPipelineBehavior<T>()`. Should `UseInbox` be a `NimBusSubscriberBuilder` method that internally feeds the `PipelineBehaviorRegistry`, or should the inbox be wired via the existing `AddPipelineBehavior<InboxMiddleware>()` plus a separate `AddNimBusInbox(...)`? Prefer the real registration shape; ratify before coding.
- **Cosmos TTL vs explicit purge.** Use container TTL for primary expiry (cheap, server-side) with `PurgeExpiredAsync` as a backstop, or rely solely on `PurgeExpiredAsync` for symmetry with SQL? Affects the Cosmos document/container provisioning.
- **Narrowing the concurrent-first-delivery window.** v1 accepts the rare double-process. A future option could add an optional "claim with short lease" mode for handlers that prefer at-most-once-leaning semantics — but only as an explicit opt-in, never the default, since it can drop messages. Not specified here.

## Resolved Questions

- **Record-on-success, not record-before-dispatch.** Resolved — this is the core correctness decision. Recording before the handler runs would skip the broker's redelivery of a transiently-failed message and silently lose it (at-most-once). The inbox checks first, dispatches, and records only after the handler succeeds, so a failed handler is always reprocessed. The accepted residual is a rare double-process under truly concurrent first delivery; `RecordProcessedAsync` is idempotent to keep that benign. This replaces the issue's single before-dispatch `TryRecordAsync` with the `HasProcessedAsync` + `RecordProcessedAsync` split.
- The inbox is opt-in, with zero overhead when not configured. Resolved — matches the issue and avoids per-message store round-trips for adapters that do not need it.
- `IInboxStore` lives in `NimBus.Core` so `InboxMiddleware` depends on the abstraction only, mirroring `IOutbox` in `NimBus.Core.Outbox`. Resolved.
- The SQL inbox mirrors `NimBus.Outbox.SqlServer` (inline `EnsureTableExistsAsync`), **not** the DbUp pattern of `NimBus.MessageStore.SqlServer`. Resolved — the issue's "DbUp numbered scripts" mis-describes the outbox; corrected.
- The Cosmos impl lives in `NimBus.MessageStore.CosmosDb`, not `NimBus.MessageStore`. Resolved — corrected project name.
- The background purge is a new `BackgroundService` modelled on `OutboxDispatcherHostedService`, since no outbox-cleanup hosted service exists to reuse. Resolved — corrected the issue's "existing OutboxCleanup host pattern".
- A duplicate maps to the existing `ResolutionStatus.Skipped` with a duplicate reason, not a new enum member. Resolved — preserves existing `resolutionStatus` filters across WebApp and CLI.
- A faulting store fails safe (propagate → abandon → redeliver), never silently skips. Resolved — the alternative drops messages.
