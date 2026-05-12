# PR #42 — Stopgap Multi-Agent Review

> Local stopgap review for [PR #42](https://github.com/akakaule/NimBus/pull/42) (`spec/004-opentelemetry-instrumentation` → `master`). Generated because `/ultrareview` wasn't intercepting in the local Claude Code install. Five subagents ran in parallel: OTel semconv compliance, public API surface, spec→test coverage, correctness of subtle code paths, and a Codex non-Claude pass. Plan: `C:\Users\aka\.claude\plans\ultrareview-42-wise-mist.md`. This is the only artifact produced; no source under review was modified.

## Executive summary

| Severity | Count | Description |
|---:|---:|---|
| **P0** | **1** | Must fix before merge — blocks merge gate |
| **P1** | **24** | Must fix or explicitly waive (user-selected gate is P0+P1) |
| P2 | 11 | Nice-to-have hardening, fine to defer |
| P3 | 6 | Informational |

**Merge-gate verdict: BLOCK.** Per the user-selected merge gate (P0 + P1 must be addressed before merge), the PR is not green. Realistically many P1s are spec-traceability gaps (constants declared but unused; "MUST" requirements with no test) rather than functional bugs — Agent 4's correctness review verified every subtle code path as clean. Triage suggestion below the findings.

### Verified clean (Agent 4 correctness review — read this if the volume below worries you)

Every property called out in the plan against the subtlest code paths verified clean against the actual code:
- `OutboxDispatcher.DispatchOneAsync` — `Activity.Current` save/clear/restore, `Dispose` ordering, `ActivityLink` carries both context and tracestate, orphan-row negative correct, inner sender parenting correct, metric tags match FR-041, `break`-on-failure ordering safe.
- `InstrumentingMessageTrackingStoreDecorator` — all 39 `IMessageTrackingStore` members routed through `InstrumentAsync`; `Verbose=false` emits zero spans; `Dispose` in finally; failure counter carries `error.type`; non-generic overload preserves exception identity.
- `NimBusGaugeBackgroundService` — `_stopped` flag prevents stale callbacks; `ConcurrentDictionary` access is race-free; INFO log fires once; cached values retained on transient failure.

So most of the P1 volume below is **test/doc/API surface drift**, not behavioural bugs.

---

## P0 blockers

### P0.1 — SC-007 (≤5% overhead at p99) has no benchmark and isn't documented as deferred

**Surface:** Success criterion — spec.md SC-007. No `tests/NimBus.OpenTelemetry.Benchmarks/` project. No `[Ignore]`-marked benchmark. No line in `phase-4.2-plan.md`'s "Known gaps" deferring it.

**Why it's P0:** SC-007 is a *success criterion* in the spec, which the spec itself treats as the merge gate. Treating an unmeasured perf budget as "passed" is the worst of both worlds — it gives a false sense of safety while preventing later regressions from being noticed.

**Fix recommendation:** Either (a) add a minimal BenchmarkDotNet project that measures publish + process overhead with and without `AddNimBusInstrumentation`, even if it's CI-opt-in; or (b) add an explicit deferral row to `phase-4.2-plan.md` carrying SC-007 forward as a Phase 4.3 / pre-stable-promotion item, with the rationale that allocation-level inspection of the hot paths shows nothing that would breach 5%. Option (b) is cheaper and honest.

---

## P1 issues

### Telemetry coverage gaps (OTel semconv compliance)

#### P1.1 — `nimbus.outbox.enqueued` counter missing required `nimbus.endpoint` attribute

**File:** `src/NimBus.Core/Outbox/OutboxSender.cs:97-102` (`BuildEnqueueTags`)
**Spec ref:** FR-041 (outbox.enqueued row declares `nimbus.endpoint` as the attribute set)

```csharp
private static KeyValuePair<string, object?>[] BuildEnqueueTags(string? eventTypeId)
{
    if (string.IsNullOrEmpty(eventTypeId))
        return Array.Empty<KeyValuePair<string, object?>>();
    return new[] { new KeyValuePair<string, object?>(MessagingAttributes.NimBusEventType, eventTypeId) };
}
```

The counter currently tags `nimbus.event_type` instead of `nimbus.endpoint`. The dispatched counter carries both `nimbus.endpoint` and `nimbus.outcome` per spec, so the asymmetry breaks endpoint-keyed dashboards on the enqueue side.

**Fix:** Pass `message.To` through `ToOutboxMessage`/`BuildEnqueueTags` and emit `nimbus.endpoint` as the canonical tag (retain `nimbus.event_type` if desired).

#### P1.2 — Dispatch counter omits `nimbus.endpoint` when payload deserialization fails

**File:** `src/NimBus.Core/Outbox/OutboxDispatcher.cs:160-171` and `:73-77`

`endpoint` is computed from `JsonConvert.DeserializeObject<Message>(outboxMessage.Payload)?.To` inside the try block. If deserialization throws, the catch path increments `nimbus.outbox.dispatched` with `nimbus.outcome=failed` but no `nimbus.endpoint`, producing a mixed metric series.

**Fix:** Persist `To`/`endpoint` on `OutboxMessage` itself at enqueue time (same path that already carries `EventTypeId`) and source `endpoint` in `DispatchOneAsync` from `outboxMessage.To` rather than the deserialized payload.

#### P1.3 — Outbox gauges (`nimbus.outbox.pending`, `nimbus.outbox.dispatch_lag`) missing required `nimbus.endpoint`

**File:** `src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs:88-96`
**Spec ref:** FR-041

Gauges are emitted as un-tagged measurements; FR-041 mandates `nimbus.endpoint`. The plan acknowledges this (`IOutboxMetricsQuery` is endpoint-agnostic by design) but does not update the spec.

**Fix:** Either extend `IOutboxMetricsQuery` with an `endpointId` parameter and bucket per endpoint, **or** amend FR-041 to remove `nimbus.endpoint` from these two gauges with a rationale (the single-table SQL outbox is intrinsically global). Option (b) matches what shipped and is the lower-effort honest fix.

#### P1.4 — `NimBus.Outbox.Dispatch` Producer span missing required `messaging.*` attributes

**File:** `src/NimBus.Core/Outbox/OutboxDispatcher.cs:80-100`
**Spec ref:** FR-010 (kind = Producer ⇒ messaging span) + FR-020 (messaging spans MUST set `messaging.system`, `messaging.operation.type`, `messaging.destination.name`)

The dispatch span currently sets only `nimbus.endpoint`, `nimbus.event_type`, and `messaging.message.id`. Backends keyed on `messaging.system` won't render the dispatch span as a producer node.

**Fix:** Set `messaging.system` (threaded through the dispatcher via DI), `messaging.operation.type="publish"`, `messaging.destination.name=endpoint`.

#### P1.5 — `messaging.message.body.size` declared but never set on any span

**File:** `src/NimBus.Core/Diagnostics/MessagingAttributes.cs:19` (constant) — referenced nowhere in publisher/consumer code paths
**Spec ref:** FR-020 (MUST)

The constant exists; no code sets it.

**Fix:** Wire body size capture in `InstrumentingSenderDecorator` (publish leg) and `ServiceBusAdapter` or `MetricsMiddleware` (process leg) from the serialized payload length, then add a test asserting the attribute is present on both spans.

#### P1.6 — `nimbus.permanent_failure`, `nimbus.delivery_count`, `nimbus.handler.type` declared but unverified

**File:** `src/NimBus.Core/Diagnostics/MessagingAttributes.cs:31-33`
**Spec ref:** FR-021

Constants exist; no test verifies the consumer span sets them.

**Fix:** Either add tests in `TelemetryCoverageTests.cs::MetricsMiddlewareInstrumentationTests` covering each case (permanent-failure outcome, delivery-count-bearing message, handler-typed dispatch), or delete the unused constants if the requirement is being walked back.

#### P1.7 — Service Bus-specific attributes (FR-023) declared but never set

**File:** `src/NimBus.Core/Diagnostics/MessagingAttributes.cs:22-25` (`ServiceBusMessageDeliveryCount`, `ServiceBusMessageEnqueuedTime`, `ServiceBusDestinationSubscriptionName`)
**Spec ref:** FR-023 ("MAY be set in addition to the generic ones")

FR-023 uses MAY (soft), but the constants are present and unused — at minimum delete them, or wire them in `ServiceBusAdapter`.

**Fix:** Wire in `ServiceBusAdapter` on the process span, or delete the constants.

#### P1.8 — `messaging.servicebus.message.session.id` missing despite FR-024 MUST

**File:** `src/NimBus.Core/Diagnostics/MessagingAttributes.cs:24` (`ServiceBusMessageSessionId`)
**Spec ref:** FR-024

`nimbus.session.key` is set; the Service Bus-specific session id is not, despite FR-024 saying "MUST be exposed as ... `messaging.servicebus.message.session.id`".

**Fix:** Set both keys when a session id is present (`MetricsMiddleware` or `ServiceBusAdapter`).

#### P1.9 — `nimbus.message.publish.duration` histogram not asserted

**File:** Coverage gap — no test asserts the histogram by name
**Spec ref:** FR-041

The histogram is recorded in `InstrumentingSenderDecorator.RecordSuccess`, but no test exercises it by name. Without an assertion, a future regression deleting the call would pass CI.

**Fix:** Add an assertion in `NimBusInstrumentationTests.cs` that `metrics.Single(m => m.Name == "nimbus.message.publish.duration")` records ≥1 observation per send.

### Public API surface (Phase 4.1 decision gate)

#### P1.10 — `IncludeMessageHeaders` and `OutboxLagWarnThreshold` are no-op options

**File:** `src/NimBus.OpenTelemetry/NimBusOpenTelemetryOptions.cs:26, 39`

Both options are public and settable but are never read anywhere except the options-binding test. Hosts that set them will silently get no behavior change. These are explicitly Phase 4.3 per spec.

**Fix:** Either (a) gate behind `[Experimental("NIMBUSOTEL001")]`, (b) defer to a `NimBusOpenTelemetryAdvancedOptions` companion class added in 4.3, or (c) at minimum add XML doc saying "Reserved for Phase 4.3 — currently no-op".

#### P1.11 — `GaugePollInterval` is captured once; `IOptionsMonitor` reload silently fails

**File:** `src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs:129`

```csharp
using var timer = new PeriodicTimer(_options.CurrentValue.GaugePollInterval);
```

The class accepts `IOptionsMonitor<NimBusOpenTelemetryOptions>` (implying live reload) but reads the value once. Hosts wiring `OnChange` listeners will see no effect.

**Fix:** Either downgrade to `IOptions<>` (no reload contract), or implement a reload listener that disposes and re-arms the timer.

#### P1.12 — `IMessageContext.ParentTraceContext` lacks a setter on the interface

**File:** `src/NimBus.Core/Messages/IMessageContext.cs:143`

```csharp
ActivityContext ParentTraceContext { get => default; }
```

Concrete implementations add a setter, but a future transport (or any third-party transport) cannot populate it via the interface contract — it must downcast.

**Fix:** Make the interface `{ get; set; }` with the default getter retained for backward compatibility, or remove the default and force implementers.

#### P1.13 — `InstrumentMessageTrackingStore` takes a raw `string storeProvider` magic string

**File:** `src/NimBus.OpenTelemetry/NimBusOpenTelemetryDecorators.cs:34`; callers pass `"cosmos"`/`"sqlserver"` literally

There's no `StoreProvider` constants class analogous to `MessagingSystem`. Future provider authors will typo this and produce orphan metric series.

**Fix:** Add a public `StoreProvider` constants class in `NimBus.Core.Diagnostics` (`Cosmos="cosmos"`, `SqlServer="sqlserver"`, `InMemory="inmemory"`).

### Test coverage gaps (spec → test map)

#### P1.14 — FR-002 package-deps invariant not enforced

**File:** No `tests/NimBus.OpenTelemetry.Tests/PackageDependencyTests.cs`
**Spec ref:** FR-002 ("MUST NOT depend on the OpenTelemetry SDK or any exporter package")

A future contributor could silently add the SDK as a dep.

**Fix:** Add a reflection test that inspects `typeof(NimBusOpenTelemetryOptions).Assembly.GetReferencedAssemblies()` and denies `OpenTelemetry` (SDK) + `OpenTelemetry.Exporter.*`.

#### P1.15 — FR-006 degraded-mode behavior (provider-builder without `IServiceCollection.AddNimBusInstrumentation`) not tested

**Spec ref:** FR-006

The spec says this MUST work; no test exercises it.

**Fix:** Add a test that builds only `MeterProviderBuilder.AddNimBusInstrumentation()` (no DI call), invokes `NimBusMeters.MessagesPublished.Add(1)` directly, and verifies it surfaces in an in-memory exporter.

#### P1.16 — FR-012 process span lifetime covers broker settle — no test

**Spec ref:** FR-012 (span ends after `Complete`/`Abandon`/`DeadLetter`/`Defer`, not after handler)

**Fix:** Add a test driving `MetricsMiddleware` with a context whose `Complete()` has a measurable delay, asserting span duration covers it.

#### P1.17 — FR-032 SQL Server outbox schema migration idempotency not tested

**File:** Plan called for a new `tests/NimBus.Outbox.SqlServer.Tests/` project — not shipped
**Spec ref:** FR-032 (idempotent ALTER TABLE migration required for existing deployments)

**Fix:** Add a SQL-gated test (under `NIMBUS_SQL_TEST_CONNECTION`) that runs `EnsureTableExistsAsync` twice and checks both columns exist via `INFORMATION_SCHEMA.COLUMNS`. Plan §1 SQL Server schema test paragraph already specifies this work.

#### P1.18 — FR-033 Baggage propagation passthrough not tested

**Spec ref:** FR-033 ("`Baggage` MUST NOT be inspected, copied into span attributes, or filtered")

**Fix:** Add a test that sets `Baggage.Current["k"]="v"` before publish, verifies no `Baggage` key leaks into span attribute keys, and asserts the value survives a round-trip untouched.

#### P1.19 — FR-043 exponential-bucket histogram aggregation not asserted

**Spec ref:** FR-043 ("histograms MUST use exponential-bucket aggregation; MUST NOT hard-code bucket boundaries")

**Fix:** Add a test asserting `AddNimBusInstrumentation` does not configure `ExplicitBucketHistogramConfiguration` for any registered instrument.

#### P1.20 — NFR-002 / NFR-003 / NFR-007 / NFR-008 / NFR-009 all UNCOVERED

**Spec ref:** various NFRs

- NFR-002 (allocation-light registration)
- NFR-003 (no exporter dependency) — covered by P1.14 fix
- NFR-007 (safe in any host)
- NFR-008 (no `ILogger` dep in hot paths) — note `NimBusGaugeBackgroundService` has a logger, but it's not a hot path
- NFR-009 (verbose-mode spans share parent traceparent)

**Fix:** Each gets a small test or arch-fitness check. NFR-009 specifically: extend `StoreDecoratorTests.cs::Decorator_opens_span_per_operation_when_Verbose_true` to assert `verboseSpan.ParentSpanId == parentFrameworkSpan.SpanId` and matching `TraceId`.

### Correctness (Codex non-Claude review)

#### P1.21 — `AddNimBusPublisher` silently reuses pre-existing `ISender` registration

**File:** `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:49`

`AddNimBusPublisher` registers the endpoint-bound sender via `TryAddSingleton<ISender>`. If a host has already registered `ISender` (for any reason), the new factory is skipped, and `IPublisherClient` (`:63`) resolves whatever was there — bypassing the endpoint binding AND the new instrumentation decorator.

**Why it matters:** A configuration mistake can silently route publishes through the wrong sender without any error. The instrumentation pretends to be wired.

**Fix:** Keep the endpoint-specific sender private to the publisher factory (e.g. a `KeyedSingleton` or an internal sender type), or document that pre-existing `ISender` registrations override `AddNimBusPublisher`.

#### P1.22 — `GetEndpointIds()` exceptions escape gauge poller

**File:** `src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs:197`

`PollDeferredAsync` calls `_deferredQuery.GetEndpointIds()` outside the per-endpoint try/catch. If endpoint discovery throws, `ExecuteAsync` faults; depending on host settings, the entire background service stops permanently.

**Fix:** Wrap endpoint discovery in the same retained-cache/log-warning policy used for per-endpoint queries. Only allow shutdown-cancellation to escape.

#### P1.23 — Singleton gauge service constructor-injects optional query providers (lifetime risk)

**File:** `src/NimBus.OpenTelemetry/Extensions/ServiceCollectionExtensions.cs:51` + `NimBusGaugeBackgroundService.cs:47-48`

`NimBusGaugeBackgroundService` is singleton via `TryAddEnumerable<IHostedService>`; its `IOutboxMetricsQuery?` / `IDeferredMessageMetricsQuery?` dependencies have no lifetime constraint. A third-party provider implemented as Scoped will either trip DI scope validation at startup or be captured from the root provider.

**Fix:** Resolve optional providers via `IServiceScopeFactory` per poll, or document/enforce singleton-only providers (with a `Validate*` call at registration time).

#### P1.24 — FR-008 canonical three-call wiring documentation pattern not validated

**Spec ref:** FR-008

The spec calls for the canonical wiring to appear in documentation. The PR doesn't ship `docs/observability.md` (FR-090 deferred to 4.3), but FR-008 isn't explicitly deferred alongside it.

**Fix:** Add FR-008 to the `phase-4.2-plan.md` "Known gaps deferred to Phase 4.3" list under the `docs/observability.md` umbrella. Pure doc fix.

---

## P2 nice-to-haves

### P2.1 — `NimBus.Outbox.Dispatch` span name violates FR-015's `{operation.type} {destination.name}` pattern

`OutboxDispatcher.cs:81` uses `"NimBus.Outbox.Dispatch"` (an internal-style name). FR-015 lists permitted internal-only spans by name; `Outbox.Dispatch` is not on that list and is `Producer`-kind, so it should be `publish {destination}`. Once endpoint is persisted on `OutboxMessage` (per P1.2), renaming is trivial.

### P2.2 — `MessagingAttributes.NimBusAuditType = "audit_type"` violates `nimbus.*` namespace contract

`MessagingAttributes.cs:38`. SC-004 / FR-021 say NimBus-specific attributes MUST use the `nimbus.*` namespace. The FR-041 table writes `audit_type` literally, so spec is internally inconsistent; FR-021 is the stronger statement. Rename to `nimbus.audit_type` (one-line literal change) — easier now than as a v2 break later.

### P2.3 — `"nimbus.outbox.batch_size"` magic string violates NFR-006

`OutboxSender.cs:93`. A `NimBusDeferredBatchSize` constant exists for the parallel deferred case at `MessagingAttributes.cs:39`. Add `MessagingAttributes.NimBusOutboxBatchSize` and use it.

### P2.4 — `IMessage.DiagnosticId` still on the public interface

`src/NimBus.Core/Messages/Models/Message.cs:49, 141`. Never written or read after FR-101. The PR is already a breaking-change release; delete or mark `[Obsolete]` to make the deprecation contract explicit.

### P2.5 — No per-field XML documentation on `MessagingAttributes` / `MessagingSystem` / `NimBusActivitySources` / `NimBusMeters` constants

NFR-005 says public types MUST have XML docs. Class-level docs are present; per-field hover is empty. One-line doc per const — low effort, high IDE payoff.

### P2.6 — `InstrumentSender(string messagingSystem)` accepts any string with no validation

`NimBusOpenTelemetryDecorators.cs`. Trivially allows `"AzureServiceBus"` (wrong casing per OTel semconv 1.41). Either accept a strongly-typed parameter or document that callers MUST use a value from `MessagingSystem`.

### P2.7 — `InstrumentationConformanceTests.MessagingSystem` (abstract property) collides with `NimBus.Core.Diagnostics.MessagingSystem` (static class)

`src/NimBus.Testing/Conformance/InstrumentationConformanceTests.cs:29`. Subclasses must fully qualify to disambiguate. Rename to `MessagingSystemValue` or `ExpectedMessagingSystem`.

### P2.8 — Gauge service overwrites `dispatch_lag` with 0 on every empty-pending poll

`NimBusGaugeBackgroundService.cs:173-176`. Correct semantically (no pending = zero lag), but a doc comment would clarify "no rows" vs "0-second lag with rows" for operators.

### P2.9 — Deferred gauge cache keeps stale `blocked_sessions` entries for removed endpoints

`NimBusGaugeBackgroundService.cs:205` + `EmitDeferred` (`:106`). When `GetBlockedSessionCountAsync` returns null on a later poll, the prior value lingers. Same for endpoints no longer in `GetEndpointIds()`.

**Fix:** Remove stale cache entries on each poll for endpoints/metrics that returned no value this cycle.

### P2.10 — Gauge service captures optional query providers from root scope

Counterpart to P1.23 — the P2 framing is "current impl works for singleton providers (which is the only kind shipped) but is fragile for third-party providers".

### P2.11 — `NimBus.Outbox.Enqueue` span doesn't carry `nimbus.endpoint`

`OutboxSender.cs:86-95`. Enqueue is `Internal`-kind so FR-020's MUSTs don't apply, but operators triaging "what got enqueued where" benefit. Optional.

---

## P3 observations

### P3.1 — `MessagingAttributes` mirrors FR-020 attribute keys exactly

No legacy forms (`messaging.destination`, `messaging.operation`, `messaging.message_id`) appear anywhere. Verified clean.

### P3.2 — `IMessageTrackingStore.GetBlockedEventsOnSession` appears in the diff but is not introduced by this PR

`src/NimBus.MessageStore.Abstractions/IMessageTrackingStore.cs:45` — branch-vs-master rebase artifact. Re-merging master will resolve. Call this out in the PR description, no code change.

### P3.3 — Observable gauge callbacks accumulate for the lifetime of the static meters

Documented at `NimBusGaugeBackgroundService.cs:39-42`; mitigated by the `_stopped` flag. Acceptable for v1; worth flagging for v2 if multi-host-in-one-process scenarios arise.

### P3.4 — `EmitDeferred` enumerates the entire deferred cache twice per export interval (once per metric)

`NimBusGaugeBackgroundService.cs:104-115`. O(2N) where N = endpoint count. Small N, not a correctness issue.

### P3.5 — `OutboxDispatcher.cs` mixes nullable and non-nullable reference annotations without `#nullable enable`

`OutboxDispatcher.cs:160` uses `string?`/`object?`; the rest of the file is unannotated. Annotations are inert. Either enable nullable file-wide or drop the `?`s.

### P3.6 — `InstrumentingMessageTrackingStoreDecorator` records duration *before* disposing the activity in `finally`

Correct ordering. Listed only to confirm the asymmetric ordering between this file and `OutboxDispatcher` is intentional (the latter records duration inside the try, before reaching finally).

---

## Triage suggestion

Per the user-selected merge gate (P0 + P1), all 25 findings must be addressed. Pragmatically, **most of the P1s collapse into a small number of meaningful changes**:

1. **One** spec-amendment commit could close ~6 P1s by deferring documentation/perf items (FR-008, FR-090 family, NFR-001/SC-007, NFR-002, NFR-007, FR-043) explicitly into Phase 4.3 — they're not actually broken, they're just not tested.
2. **One** "outbox endpoint plumbing" commit closes P1.1, P1.2, P1.3 (and P2.1, P2.11) by persisting `To` on `OutboxMessage` and threading it through every emit site.
3. **One** "Service Bus attribute coverage" commit closes P1.5, P1.6, P1.7, P1.8 by either wiring the documented attributes or deleting the unused constants.
4. **One** "test gap" commit closes P1.9, P1.14, P1.15, P1.16, P1.17, P1.18, P1.19, P1.20 by adding small unit tests / reflection assertions.
5. **One** "public API hardening" commit closes P1.10, P1.11, P1.12, P1.13, P1.24 (docs/option semantics, options-monitor contract, `ParentTraceContext` setter, `StoreProvider` constants class).
6. **One** "DI lifetime + safety" commit closes P1.21, P1.22, P1.23 (publisher `ISender` shadowing, endpoint-discovery exception escape, gauge-service provider lifetime).

That's roughly **6 follow-up commits** to clear the merge gate, none of them large. Alternatively, downgrade the gate to P0-only (which clears with one spec amendment for SC-007) and ship the P1s as a Phase 4.2.1 follow-up PR.

---

## Provenance

- Plan: `C:\Users\aka\.claude\plans\ultrareview-42-wise-mist.md`
- Agents:
  - Agent 1 (OTel semconv compliance) — `general-purpose` (Claude Sonnet)
  - Agent 2 (public API surface) — `general-purpose`
  - Agent 3 (spec → test coverage map) — `general-purpose`
  - Agent 4 (correctness of subtle paths) — `general-purpose`; result: **verified clean on every property checked**
  - Agent 5 (non-Claude pass) — `codex:codex-rescue`; Codex session `019e15ab-622e-7402-b17c-ccb8ae1ea2c0`
- All five agents ran read-only against `master..HEAD` of `c:\Git\NimBus`.
- This document is the only artifact; no source under review was modified.
