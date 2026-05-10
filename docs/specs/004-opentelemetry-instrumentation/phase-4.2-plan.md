# Phase 4.2 — Implementation Plan

> Companion to `spec.md`. Phase 4.1 shipped in commit `d40bb44` (`NimBus.OpenTelemetry` package, `AddNimBusInstrumentation`, publisher-side `InstrumentingSenderDecorator`, `messaging.*` semantic-convention alignment, deletion of legacy diagnostics). Phase 4.2 covers the **internal NimBus components** — outbox, deferred-message processor, resolver, message store — plus W3C trace-context restoration through the outbox and the gauge background service.
>
> Spec 003 (RabbitMQ) is cancelled, so the FR-085 transport-conformance category becomes a single-transport assertion (in-memory + Service Bus only) rather than a cross-transport parity gate.

## Status — Phase 4.2 complete

| | Component | Commit |
|---|---|---|
| ✅ | §1 Outbox W3C trace-context capture + ActivityLink restore + enqueue/dispatch spans + metrics + `IOutboxMetricsQuery` contract | `698e68e` |
| ✅ | §2 Deferred-processor park + replay spans + counters + duration histogram | `dcdac60` |
| ✅ | §3 Resolver outcome + audit spans + counters + write.duration histogram | `21cf905` |
| ✅ | §4 `IMessageTrackingStore` decorator (FR-055) | `668eb4b` |
| ✅ | §5 `NimBusGaugeBackgroundService` + outbox/deferred gauges (FR-044, FR-052) | `931968b` |
| ✅ | §6 Single-transport conformance harness — in-memory + Service Bus (Inconclusive) (FR-085 scoped down) | `f5eaf06` |

**Phase 4.1 follow-up resolved:** FR-056 (`NimBus.Resolver` Function host depends on `NimBus.OpenTelemetry`) is satisfied transitively via `NimBus.Resolver` → `NimBus.ServiceDefaults` → `NimBus.OpenTelemetry`, with `AddServiceDefaults()` calling the canonical three-call wiring. No code change was needed.

**Known gaps deferred to Phase 4.3 / follow-ups:**

- **FR-020 consumer-side `messaging.system`** — `MetricsMiddleware` does not currently set `messaging.system` on the process span because it has no transport-identity injected. Closing requires either a constructor parameter on `MetricsMiddleware` (transport pipelines pass through) or a per-context transport identity property. Conformance harness asserts the attribute on publish only.
- **Service Bus live-broker conformance run** — the `ServiceBusInstrumentationConformanceTests` skeleton gates correctly on `NIMBUS_SERVICEBUS_TEST_CONNECTION` but the live publish leg is `Assert.Inconclusive` even when the env var is set. Wiring up a real `ServiceBusClient` round-trip is a separate task.
- **Per-endpoint outbox gauges** — `IOutboxMetricsQuery` is endpoint-agnostic by design (the SQL Server outbox is one table across all endpoints), so the `nimbus.outbox.pending` gauge is currently a single global series. If per-endpoint breakdowns become a requirement, the contract needs an `endpointId` parameter and the SQL Server schema needs a denormalised `EndpointId` column.

## Context

Phase 4.1 already declared every Phase 4.2 instrument in `NimBus.Core.Diagnostics.NimBusMeters` (`OutboxEnqueued`, `OutboxDispatched`, `OutboxDispatchDuration`, `DeferredParked`, `DeferredReplayed`, `DeferredReplayDuration`, `ResolverOutcomeWritten`, `ResolverAuditWritten`, `ResolverWriteDuration`, `StoreOperationDuration`, `StoreOperationFailed`) and every activity source (`NimBusActivitySources.Outbox`, `.DeferredProcessor`, `.Resolver`, `.Store`). Phase 4.2 wires call-sites and adds the gauge service, the store decorator, the outbox metrics-query interface, and the W3C plumbing.

The plan is grouped by component. Each component lists production edits and the tests that must land with it.

## Components

### 1. Outbox — capture and restore W3C trace context (FR-032) [DONE — `698e68e`]

> **Note (post-implementation):** the `IOutboxMetricsQuery` interface and its `SqlServerOutbox` implementation shipped in this commit. The runtime side — the gauge background service that *uses* the contract — is deferred to §5. So §1 covers FR-032 + FR-014 + the contract slice of FR-052.

The current outbox row carries `CorrelationId` only; that is insufficient for W3C propagation. Without this work, every message that goes through the outbox starts a new trace at the dispatcher with no link to the original publisher.

**Production edits:**

- `src/NimBus.Core/Outbox/OutboxMessage.cs` — add `string? TraceParent` and `string? TraceState` (nullable; backwards-compatible with rows persisted before the migration).
- `src/NimBus.Core/Outbox/OutboxSender.cs` — in `ToOutboxMessage`, capture `Activity.Current?.Id` and `Activity.Current?.TraceStateString` into the new fields. Wrap the `_outbox.StoreAsync`/`StoreBatchAsync` calls in a `NimBus.Outbox.Enqueue` span (`ActivityKind.Internal`, source `NimBusActivitySources.Outbox`) and increment `NimBusMeters.OutboxEnqueued` once the write returns.
- `src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs` — extend the schema:
  - In `EnsureTableExistsAsync`, add `[TraceParent] NVARCHAR(55) NULL` and `[TraceState] NVARCHAR(256) NULL` to the `CREATE TABLE`.
  - Append an idempotent `IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD ...` migration block that runs after the create-if-not-exists for existing deployments. Do not gate on a feature flag — the columns are always added.
  - Update every `INSERT` (`StoreAsync`, both branches of `StoreBatchAsync`) to bind `@TraceParent` and `@TraceState`. Update every `SELECT` (in `GetPendingAsync`) to read them into `OutboxMessage`.
  - Update `AddOutboxMessageParameters` to bind the two new columns.
- `src/NimBus.Core/Outbox/OutboxDispatcher.cs` — for each pending row:
  - Open a `NimBus.Outbox.Dispatch` span (`ActivityKind.Producer`, source `NimBusActivitySources.Outbox`) as a **root span** so it does not nest under whatever activity the dispatcher poll loop is currently in. The shipped implementation saves and clears `Activity.Current` around `StartActivity` so the new span has no parent — restoring `Activity.Current` in `finally`.
  - When `outboxMessage.TraceParent` is non-null, parse it (`ActivityContext.TryParse`) and pass the parsed context as an **`ActivityLink`** when starting the dispatch span. This is the "causal-but-not-temporal" wiring from FR-014 and acceptance scenario 2.4 of User Story 2 — the dispatch span links to the original publisher's context without nesting under it.
  - When `TraceParent` is null (pre-migration row), emit `activity?.AddEvent(new ActivityEvent("nimbus.outbox.orphan_row"))` and proceed without a link. This is FR-032's pre-existing-rows clause.
  - The inner `_sender.Send` / `ScheduleMessage` call runs while the dispatch span is `Activity.Current` (StartActivity sets it for us), so the publisher span emitted by `InstrumentingSenderDecorator` from Phase 4.1 becomes a **child of the dispatch span** within the dispatch span's own trace. The original publisher's context is reachable only through the link.
  - On success: increment `NimBusMeters.OutboxDispatched` (with `nimbus.endpoint` and `nimbus.outcome=dispatched`) and record `NimBusMeters.OutboxDispatchDuration`. Set `ActivityStatusCode.Ok`.
  - On exception: record the exception per OTel convention (existing pattern from `InstrumentingSenderDecorator.RecordFailure`), increment `OutboxDispatched` with `nimbus.outcome=failed` and `error.type`, **then** the existing `break`-on-error logic continues unchanged.
- `src/NimBus.Core/Outbox/IOutbox.cs` — **add a separate interface** (do not pollute `IOutbox`):

  ```csharp
  public interface IOutboxMetricsQuery
  {
      Task<long> GetPendingCountAsync(CancellationToken ct);
      Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken ct);
  }
  ```

  Implement on `SqlServerOutbox` via two single-statement queries (`SELECT COUNT(*) ... WHERE DispatchedAtUtc IS NULL`; `SELECT TOP 1 CreatedAtUtc ... WHERE DispatchedAtUtc IS NULL ORDER BY CreatedAtUtc ASC`). Register in `ServiceCollectionExtensions` of `NimBus.Outbox.SqlServer` as a singleton resolved from the same `SqlServerOutbox` instance. When no provider implements it, the gauge service emits no values for the corresponding gauges (FR-052) and logs a one-time INFO.

**Tests** (`tests/NimBus.OpenTelemetry.Tests/`, new file `OutboxInstrumentationTests.cs`):

- `OutboxSender_emits_enqueue_span_and_captures_trace_context` — set `Activity.Current`, call `Send`, assert the persisted `OutboxMessage` has the correct `TraceParent` / `TraceState`, and one `NimBus.Outbox.Enqueue` span was emitted.
- `OutboxDispatcher_links_original_traceparent_via_ActivityLink` — pre-populate the in-memory outbox with a row that has a non-null `TraceParent`; dispatch; assert the resulting `NimBus.Outbox.Dispatch` span has exactly one `ActivityLink` whose context matches the captured `TraceParent`. The publisher span is the dispatch span's child.
- `OutboxDispatcher_orphan_row_emits_event_and_succeeds` — pre-populate a row with `TraceParent = null`; dispatch; assert the dispatch span has zero links, an `ActivityEvent` named `nimbus.outbox.orphan_row`, and the inner sender was invoked.
- `OutboxDispatcher_failure_records_error_and_increments_failed_outcome` — inner sender throws; assert span error status, `nimbus.outbox.dispatched` counter increments with `nimbus.outcome=failed` + `error.type`.

**SQL Server schema test** in `tests/NimBus.MessageStore.SqlServer.Tests/` already runs the schema initializer; add a `SqlServerOutboxSchemaTests` (new file in a new project `tests/NimBus.Outbox.SqlServer.Tests/` per the test-debt P0 from the analysis plan — this test fits there) asserting both columns exist after `EnsureTableExistsAsync` and that the migration is idempotent on a pre-migration table.

### 2. DeferredMessageProcessor — park and replay spans (FR-053) [DONE — `dcdac60`]

> **Plan correction (post-implementation):** an earlier draft of this section claimed the parking call site was `MessageContext.BlockSession`. That was wrong. `BlockSession` only flips a session-state flag — it does not write to the deferred subscription. The actual park site is `ResponseService.SendToDeferredSubscription` in `NimBus.Core.Messages`, called from `StrictMessageHandler.DeferMessageToSubscription`. The instrumentation lives there. Tag set was kept (`nimbus.endpoint`, `nimbus.session.key`, `nimbus.event_type`); the planned `messaging.system=servicebus` tag was dropped because `ResponseService` is in `NimBus.Core` and does not know the transport.

**Production edits (as shipped):**

- `src/NimBus.Core/Messages/ResponseService.cs::SendToDeferredSubscription` — opens `NimBus.DeferredProcessor.Park` (`ActivityKind.Internal`, source `NimBusActivitySources.DeferredProcessor`). Tags: `nimbus.endpoint = messageContext.To`, `nimbus.session.key = messageContext.SessionId`, `nimbus.event_type = messageContext.EventTypeId`. On success: increments `NimBusMeters.DeferredParked` with `nimbus.endpoint` only (sessions stay off metrics per FR-045). On failure: sets `ActivityStatusCode.Error` + `error.type` and re-throws — counter does not increment.
- `src/NimBus.ServiceBus/DeferredMessageProcessor.cs::ProcessDeferredMessagesAsync` — wraps the entire session-scoped replay in a single `NimBus.DeferredProcessor.Replay` span. Tags: `nimbus.endpoint = topicName`, `nimbus.session.key = sessionId`. Per batch: `NimBusMeters.DeferredReplayed += orderedMessages.Count`, `NimBusMeters.DeferredReplayDuration` records batch elapsed ms (both tagged with `nimbus.endpoint` only). Span's `nimbus.deferred.batch_size` is set in `finally` to the cumulative count, so SessionCannotBeLocked / empty-batch / transient-failure paths all carry the correct value (the early-return SessionCannotBeLocked branch also sets `Ok` status so it ends as a graceful no-op rather than `Unset`).

**Tests (as shipped):** stdlib `ActivityListener` / `MeterListener` (no OTel SDK package needed in either test project).

- `tests/NimBus.Core.Tests/ResponseServiceTests.cs` — added `SendToDeferredSubscription_emits_park_span_and_increments_counter`, `SendToDeferredSubscription_failure_records_error_status_and_error_type_tag`. Park lives in Core, so tests live next to `ResponseServiceTests` and reuse the existing `RecordingSender`/`FakeMessageContext` doubles.
- `tests/NimBus.ServiceBus.Tests/DeferredMessageProcessorTests.cs` — added `ProcessDeferredMessagesAsync_emits_replay_span_and_increments_counter`, `..._no_messages_records_zero_batch_size`, `..._session_cannot_be_locked_records_zero_batch_size`, `..._transient_failure_records_error_status`. Reuses the existing `RecordingServiceBusClient` double from `ServiceBusTestDoubles.cs`.

### 3. ResolverService — outcome and audit instrumentation (FR-054) [DONE — `21cf905`]

> **Plan correction (post-implementation):** an earlier draft put tests in `tests/NimBus.OpenTelemetry.Tests/ResolverInstrumentationTests.cs`. They actually shipped at `tests/NimBus.Resolver.Tests/ResolverInstrumentationTests.cs` so they could reuse the existing `FakeCosmosDbClient` harness (~40 method stubs would have to be duplicated otherwise). The harness gained optional `UploadException` and `StoreAuditException` hooks for the failure paths; visibility on `FakeMessageContext`, `FakeCosmosDbClient`, `UploadCall` was lifted from `private` to `internal` so the new test class could see them. Stdlib `ActivityListener` / `MeterListener` is used so `NimBus.Resolver.Tests` does not need OpenTelemetry SDK packages.

**Production edits to `src/NimBus.Resolver/Services/ResolverService.cs`:**

- In `UpdateState`, wrap the `await handler()` invocation (the upload to the store) in a `NimBus.Resolver.RecordOutcome` span (`ActivityKind.Internal`, source `NimBusActivitySources.Resolver`) with attributes `nimbus.endpoint`, `nimbus.outcome=<lower-cased status>`. After `await handler()`, increment `NimBusMeters.ResolverOutcomeWritten` (tags: `nimbus.endpoint`, `nimbus.outcome`) and record `NimBusMeters.ResolverWriteDuration`.
- In `CreateMessageEntity`, the `RetryRequest` branch calls `_store.StoreMessageAudit`. Wrap it (and any future audit writes) in a `NimBus.Resolver.RecordAudit` span and increment `NimBusMeters.ResolverAuditWritten` (tags: `nimbus.endpoint`, `audit_type=<lower-cased AuditType>`).
- Failure paths (`StorageProviderTransientException`, `TransientException`, generic `Exception`) — set the active span's `ActivityStatusCode.Error` per OTel convention. Don't double-emit: the surrounding consumer span already records the message-level failure; the resolver span is just for the per-write timing.

The existing wall-clock dependency in `ResolverServiceTests.cs:155–158` (flagged in the test analysis plan) is **out of scope for Phase 4.2** but the resolver edit is a natural place to inject `TimeProvider` for the throttling backoff calculation. Defer that to its own change unless the diff is trivial; do not bundle.

**Tests** (`tests/NimBus.OpenTelemetry.Tests/ResolverInstrumentationTests.cs`):

- `Resolver_records_outcome_span_and_counter_per_status` — drive `ResolverService` via the existing `FakeCosmosDbClient` test harness with one message per terminal status (Completed / Skipped / Failed / Deferred / Pending / DeadLettered / Unsupported) and assert one span and one counter increment per status, tagged correctly.
- `Resolver_records_audit_span_for_RetryRequest` — feed a `RetryRequest`; assert one `NimBus.Resolver.RecordAudit` span with `audit_type=retry` and a +1 audit counter.
- `Resolver_throttle_failure_records_span_error_status` — make the fake store throw `StorageProviderTransientException`; assert the outcome span ends with `ActivityStatusCode.Error` and the counter still increments with `error.type` (failure attribution per FR-022).

### 4. Message-store decorator (FR-055) [DONE — `668eb4b`]

**Production edits — new file `src/NimBus.OpenTelemetry/Instrumentation/InstrumentingMessageTrackingStoreDecorator.cs`:**

A pass-through decorator that times every method on `IMessageTrackingStore`. The interface has ~30 methods — implement them as a small set of generic helpers:

```csharp
private async Task<T> InstrumentAsync<T>(string operation, Func<Task<T>> inner) { ... }
private async Task InstrumentAsync(string operation, Func<Task> inner) { ... }
```

Each helper opens a `NimBus.Store.{operation}` span **only when `options.Verbose == true`** (per FR-010), records `NimBusMeters.StoreOperationDuration` with tags `nimbus.store.operation` and `nimbus.store.provider`, and on exception increments `NimBusMeters.StoreOperationFailed` with `error.type` plus the same tags.

**Factory:** extend `src/NimBus.OpenTelemetry/NimBusOpenTelemetryDecorators.cs`:

```csharp
public static IMessageTrackingStore InstrumentMessageTrackingStore(
    IMessageTrackingStore inner, string storeProvider) =>
    new InstrumentingMessageTrackingStoreDecorator(inner, storeProvider);
```

**Wiring:** the storage extensions register the decorator outside the `IMessageTrackingStore` registration chain so the decorator wraps the concrete store but the other three contracts (`ISubscriptionStore`, `IEndpointMetadataStore`, `IMetricsStore`) still resolve through the same `INimBusMessageStore` instance.

- `src/NimBus.MessageStore.SqlServer/SqlServerMessageStoreBuilderExtensions.cs:56` — replace the line:

  ```csharp
  services.AddSingleton<IMessageTrackingStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
  ```

  with:

  ```csharp
  services.AddSingleton<IMessageTrackingStore>(sp =>
      NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
          sp.GetRequiredService<INimBusMessageStore>(), "sqlserver"));
  ```

- `src/NimBus.MessageStore.CosmosDb/CosmosDbMessageStoreBuilderExtensions.cs:61` — same change with `"cosmos"` as the provider tag.

- For the in-memory store (`NimBus.Testing`'s `InMemoryMessageStore`), wire the decorator inside `NimBusTestFixture` only. Production code does not register an in-memory store; tests can opt in if they want store-metric assertions.

**Tests** (`tests/NimBus.OpenTelemetry.Tests/StoreDecoratorTests.cs`):

- `Store_decorator_records_duration_per_operation` — wrap an `InMemoryMessageStore`; call `UploadCompletedMessage`; assert exactly one histogram observation with `nimbus.store.operation=UploadCompletedMessage` and `nimbus.store.provider=test`.
- `Store_decorator_records_failure_with_error_type` — wrap a fake store that throws; assert the failure counter increments with `error.type`.
- `Store_decorator_does_not_open_span_when_Verbose_false` — default options; call any method; assert zero `NimBus.Store` spans.
- `Store_decorator_opens_span_when_Verbose_true` — set `options.Verbose = true`; assert one span per call. (Verbose-mode wiring lands in 4.3; this test exercises the option flag now so the 4.3 turn-on is a config change, not a code change.)

### 5. Gauge background service (FR-044) [DONE — `931968b`]

**New file `src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs`:**

A `BackgroundService` that registers `ObservableGauge<long>` instruments on the shared meters:

- `nimbus.outbox.pending` (`NimBusMeters.Outbox`) — value pulled from `IOutboxMetricsQuery.GetPendingCountAsync`. Tag: `nimbus.endpoint`.
- `nimbus.outbox.dispatch_lag` (`NimBusMeters.Outbox`, unit `s`) — value derived from `IOutboxMetricsQuery.GetOldestPendingEnqueuedAtUtcAsync` (`now - oldest`). Tag: `nimbus.endpoint`.
- `nimbus.deferred.pending` (`NimBusMeters.DeferredProcessor`) — value pulled from a new `IDeferredMessageMetricsQuery` interface (defined alongside `IOutboxMetricsQuery`). The Service Bus implementation queries the deferred subscription via the existing `ServiceBusAdministrationClient` for the queue's `ActiveMessageCount` on the deferred subscription. Tag: `nimbus.endpoint`.
- `nimbus.deferred.blocked_sessions` (`NimBusMeters.DeferredProcessor`) — value pulled from `IMessageTrackingStore` via a new helper `GetBlockedSessionCountAsync(string endpointId)` or, simpler, count distinct `SessionId` values in `GetPendingEventsOnSession`. Defer the helper if it adds complexity — start by registering only the two outbox gauges and the deferred-pending gauge from the broker; ship `blocked_sessions` if cheap, drop if not.

Polling cadence: when an instrument's callback fires, it triggers an async query against the registered query interfaces. The OTel SDK reads observable gauges on its own export interval, but the spec mandates a **dedicated poll** at `NimBusOpenTelemetryOptions.GaugePollInterval` (default 30s) so we cache the most-recent value and the gauge callback returns the cached value synchronously (the OTel callback must not block). Implementation: `BackgroundService` runs a `PeriodicTimer(GaugePollInterval)` loop, queries each provider, stores results in a `volatile` cache; gauge callbacks read from the cache.

Skip-if-missing behaviour: when no `IOutboxMetricsQuery` is registered, log once at INFO ("nimbus.outbox.pending and nimbus.outbox.dispatch_lag will not be reported because no IOutboxMetricsQuery is registered") and skip the corresponding gauge registrations (FR-052).

**Wiring:** register from `AddNimBusInstrumentation` (`src/NimBus.OpenTelemetry/Extensions/ServiceCollectionExtensions.cs`):

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NimBusGaugeBackgroundService>());
```

`TryAddEnumerable` keeps the registration idempotent for the existing idempotency contract.

**Tests** (`tests/NimBus.OpenTelemetry.Tests/GaugeBackgroundServiceTests.cs`):

- `Gauges_report_outbox_pending_count` — register a fake `IOutboxMetricsQuery` returning `42`; start the hosted service; force a flush; assert the `nimbus.outbox.pending` gauge observation equals `42`.
- `Gauges_report_dispatch_lag_in_seconds` — fake returns `now - 90s`; assert the `dispatch_lag` gauge ≈ 90.
- `Gauges_skipped_when_query_unregistered` — no `IOutboxMetricsQuery`; assert no `nimbus.outbox.pending` observations and one INFO log line.
- `Gauges_use_cached_value_between_polls` — fake's call count after 1s with `GaugePollInterval = 5s` is 1 (initial poll only); the gauge callback may have fired multiple times.

### 6. Conformance category (FR-085, scoped down) [DONE — `f5eaf06`]

Spec 003 is cancelled, so FR-085's "transport-conformance instrumentation category" reduces to: a single set of assertions that runs against the in-memory transport (always available in CI) and Service Bus (when `NIMBUS_SERVICEBUS_TEST_CONNECTION` is set).

**Production edits — none.** The existing `NimBus.Testing` conformance harness gets a new abstract base class:

- `src/NimBus.Testing/Conformance/InstrumentationConformanceTests.cs` (new) — abstract `[TestClass]` with virtual factories for the transport under test. Test methods cover:
  - One publish → one consume produces one trace id with producer→consumer parent-child.
  - Outbox publish → dispatch → consume produces a trace where the dispatcher span has the original publish's context as `ActivityLink` (FR-014, SC-010).
  - Both legs of the round-trip emit the documented `messaging.*` attributes.

- `tests/NimBus.OpenTelemetry.Tests/InMemoryInstrumentationConformanceTests.cs` (new) — concrete subclass running against `InMemoryMessageBus`.
- `tests/NimBus.ServiceBus.Tests/ServiceBusInstrumentationConformanceTests.cs` (new) — concrete subclass that `Assert.Inconclusive`s when the connection-string env var is missing, otherwise runs against a real namespace. Mirror the existing harness skip pattern from `SqlServerStoreTestHarness`.

## Files touched (summary)

**Production:**
- `src/NimBus.Core/Outbox/OutboxMessage.cs` — add `TraceParent`, `TraceState`.
- `src/NimBus.Core/Outbox/OutboxSender.cs` — capture trace context, emit enqueue span + counter.
- `src/NimBus.Core/Outbox/OutboxDispatcher.cs` — emit dispatch span with `ActivityLink`, restore trace context, emit metrics.
- `src/NimBus.Core/Outbox/IOutbox.cs` — add `IOutboxMetricsQuery` interface in same file or sibling.
- `src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs` — schema migration, parameter binding, `IOutboxMetricsQuery` impl.
- `src/NimBus.Outbox.SqlServer/ServiceCollectionExtensions.cs` — register `IOutboxMetricsQuery`.
- `src/NimBus.Core/Messages/ResponseService.cs` — park span + counter in `SendToDeferredSubscription` (the actual call site that writes to the deferred subscription, called from `StrictMessageHandler.DeferMessageToSubscription`; an earlier draft of this plan misidentified it as `MessageContext.BlockSession`).
- `src/NimBus.ServiceBus/DeferredMessageProcessor.cs` — replay span + counter + duration histogram.
- `src/NimBus.Resolver/Services/ResolverService.cs` — outcome + audit spans, counters, duration histograms.
- `src/NimBus.OpenTelemetry/Instrumentation/InstrumentingMessageTrackingStoreDecorator.cs` — new file.
- `src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs` — new file.
- `src/NimBus.OpenTelemetry/NimBusOpenTelemetryDecorators.cs` — add `InstrumentMessageTrackingStore` factory.
- `src/NimBus.OpenTelemetry/Extensions/ServiceCollectionExtensions.cs` — register hosted service.
- `src/NimBus.MessageStore.SqlServer/SqlServerMessageStoreBuilderExtensions.cs:56` — wrap decorator.
- `src/NimBus.MessageStore.CosmosDb/CosmosDbMessageStoreBuilderExtensions.cs:61` — wrap decorator.

**Tests:**
- `tests/NimBus.OpenTelemetry.Tests/OutboxInstrumentationTests.cs` — new.
- `tests/NimBus.OpenTelemetry.Tests/DeferredProcessorInstrumentationTests.cs` — new.
- `tests/NimBus.OpenTelemetry.Tests/ResolverInstrumentationTests.cs` — new.
- `tests/NimBus.OpenTelemetry.Tests/StoreDecoratorTests.cs` — new.
- `tests/NimBus.OpenTelemetry.Tests/GaugeBackgroundServiceTests.cs` — new.
- `tests/NimBus.OpenTelemetry.Tests/InMemoryInstrumentationConformanceTests.cs` — new.
- `tests/NimBus.ServiceBus.Tests/ServiceBusInstrumentationConformanceTests.cs` — new.
- `src/NimBus.Testing/Conformance/InstrumentationConformanceTests.cs` — new abstract base.

## Sequencing — as shipped

Each step is independently shippable and tests a discrete acceptance scenario. The original draft proposed shipping the gauge service immediately after the outbox work; in practice the `IOutboxMetricsQuery` *contract* shipped with §1 and the gauge background service that consumes it landed in §5 once the resolver, deferred, and store work was already in place — that ordering kept each commit reviewer-friendly without losing coverage.

1. **§1 Outbox schema + trace-context capture/restore + enqueue/dispatch metrics + spans + `IOutboxMetricsQuery` contract** (User Story 4 acceptance #1; User Story 2 acceptance #4). Commit `698e68e`.
2. **§3 Resolver outcome + audit instrumentation** (User Story 4 acceptance #4–#6). Commit `21cf905`.
3. **§2 Deferred processor park + replay instrumentation** (User Story 4 acceptance #2–#3). Commit `dcdac60`.
4. **§4 `InstrumentingMessageTrackingStoreDecorator` + storage extension wiring** (FR-055). Commit `668eb4b`.
5. **§5 `NimBusGaugeBackgroundService` — outbox pending/lag + deferred pending/blocked-sessions gauges** (FR-044, FR-052 runtime side). Commit `931968b`.
6. **§6 Instrumentation conformance harness + in-memory and Service Bus concrete subclasses** (FR-085, scoped down). Commit `f5eaf06`.

## Verification

- `dotnet test src/NimBus.sln` runs green locally and in CI.
- `NIMBUS_SQL_TEST_CONNECTION` covers the new outbox schema migration in CI.
- Acceptance scenarios for User Story 4 (FR-014, FR-052, FR-053, FR-054, FR-055) verifiable by running the AspirePubSub sample with OTel exporters and observing the documented spans/metrics in the Aspire dashboard.
- SC-003 (every component emits ≥ 1 span and ≥ 1 metric) verified by the new conformance harness.
- SC-010 (`ActivityLink` on dispatcher span) verified by `OutboxDispatcher_links_original_traceparent_via_ActivityLink`.

## Out of scope for Phase 4.2 (deferred to 4.3)

- Verbose-mode store spans (the test stub for `Verbose = true` lands now; the per-pipeline-step spans of FR-122 wait for 4.3).
- `IncludeMessageHeaders` option behaviour.
- Aspire / Grafana sample dashboards.
- Migration documentation (`docs/observability.md`).

## Out of scope by cancellation

- Anything that depended on spec 003 (RabbitMQ): cross-transport conformance, `messaging.system=rabbitmq` assertions, on-prem dual-transport samples. The conformance harness is single-transport per the §6 note above.
