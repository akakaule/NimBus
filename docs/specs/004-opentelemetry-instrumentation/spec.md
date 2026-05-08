# Feature Specification: First-Class OpenTelemetry Instrumentation Package

Feature Branch: `004-opentelemetry-instrumentation`
Created: 2026-05-08
Updated: 2026-05-08
Status: Draft
Tracking issue: TBD
Driving ADR: TBD (proposed: ADR-013 — *NimBus.OpenTelemetry as the canonical instrumentation surface*)
Input: User description: *"First-class OpenTelemetry instrumentation package — a `NimBus.OpenTelemetry` NuGet that registers every NimBus meter and tracer with one call, emits `messaging.*` semantic-convention tags, and instruments not just the receive path but also the publish path, the outbox dispatcher, the deferred-message processor, the resolver, and the message store."*

## Scope (resolved)

NimBus today has *partial* instrumentation: a single `NimBus.Process` consumer span emitted from `ServiceBusAdapter`, two ad-hoc meters (`NimBus.ServiceBus`, `NimBus.Pipeline`), and W3C trace propagation via the `Diagnostic-Id` Service Bus application property. The instrumentation is incomplete (no publish span, no outbox / deferred / resolver / store spans), the metric names are not aligned with OTel `messaging.*` semantic conventions, and there is no shipping NuGet package — operators wire each meter / source by hand in their own host. This feature ships a single package that consolidates instrumentation across NimBus, follows OTel `messaging.*` conventions, and registers the full set with one builder call.

## Provider Scope (resolved)

The package is **transport-agnostic and storage-agnostic**. It registers the meter and tracer names that all NimBus components emit. Both Service Bus (today) and the future RabbitMQ transport (spec 003) use identical instrumentation surface — `messaging.system` is the only attribute that differentiates them. Both the Cosmos DB and SQL Server storage providers emit the same `nimbus.store.*` instruments via `IMessageTrackingStore` interception.

## Critical Design Insight (resolved)

Instrumentation today lives **inside the transport adapter** (`ServiceBusAdapter.HandleWithLatencyTracking`). That placement is the reason the publish path is uninstrumented (publishers go through `Sender`, not the adapter), the outbox dispatcher is uninstrumented (it hands a message to `ISender` without a span), the deferred processor is uninstrumented, and the resolver is uninstrumented. Instrumentation MUST move to the **shared abstractions**:

- `ISender` decorator emits `NimBus.Publish` and the publish counters / histograms.
- `IMessagePipelineBehavior` (existing `MetricsMiddleware`) carries the consumer span and the per-handler counters / histograms.
- `OutboxSender` / `OutboxDispatcher` emit `NimBus.Outbox.*` instruments.
- `DeferredMessageProcessor` emits `NimBus.DeferredProcessor.*` instruments.
- `ResolverService` emits `NimBus.Resolver.*` instruments.
- `IMessageTrackingStore` decorator emits `NimBus.Store.*` instruments.

The single transport adapter retains responsibility for *transport-level* attributes (broker enqueued time, native message id, broker-side delivery count) but not for owning the span lifecycle.

## User Scenarios & Testing

### User Story 1 — One-line registration (Priority: P1)

As a developer setting up observability for a NimBus service, I want a single extension method that registers every NimBus meter, tracer source, and propagator with the OpenTelemetry SDK, so that I do not have to hard-code meter names that may change between releases.

Why this priority: Today consumers must know the internal names `"NimBus.ServiceBus"` and `"NimBus.Pipeline"` and the activity source `"NimBus"`. That coupling breaks across upgrades — `NimBus.ServiceDefaults` already drifted out of sync with the `NimBus.Pipeline` meter. A single extension method removes the coupling.

Independent Test: Create a fresh ASP.NET Core project, add `NimBus.OpenTelemetry`, call `tracing.AddNimBusInstrumentation()` and `metrics.AddNimBusInstrumentation()`. Run the AspirePubSub sample. Verify that all NimBus instruments documented in §*Metrics* and §*Traces* are exported via the OTLP collector without naming any meter or source explicitly.

Acceptance Scenarios:

1. Given a service registers `metrics.AddNimBusInstrumentation()`, When messages flow through the service, Then every NimBus metric documented in §*Metrics* is exported.
2. Given a service registers `tracing.AddNimBusInstrumentation()`, When a message is published and consumed, Then a `NimBus.Publish` span and a `NimBus.Process` span are exported with the correct parent-child relationship.
3. Given a service registers neither, When messages flow, Then no NimBus instruments are exported (the SDK ignores unregistered meters / sources).
4. Given the `NimBus.OpenTelemetry` package is referenced but the OTel SDK is not configured, When the service starts, Then nothing breaks — meters and sources are passive when no listener is attached.

---

### User Story 2 — Publish-side instrumentation parity with consume-side (Priority: P1)

As an operator debugging end-to-end latency, I want a `NimBus.Publish` span on the producer side that is the parent of the matching `NimBus.Process` span on the consumer side, with consistent `messaging.message.id`, `messaging.destination.name`, and `messaging.message.conversation_id` attributes, so that I can follow a single trace from `ISender.Send` through every hop.

Why this priority: Today the consumer span exists but no producer span does, so traces start at the consumer with no parent and operators cannot visualize publish latency or outbox dispatch latency.

Independent Test: Run a sample where service A publishes a message and service B consumes it. Open the resulting trace. Verify the trace has at least two spans (`NimBus.Publish` from A, `NimBus.Process` from B), that they are part of the same trace id, and that the `messaging.message.id` attribute is present and identical on both spans.

Acceptance Scenarios:

1. Given a publisher calls `ISender.Send(message)`, When the message is sent, Then a `NimBus.Publish` span with `ActivityKind.Producer` is emitted.
2. Given the message is consumed, When the consumer span starts, Then it has the publish span's `traceparent` as its parent (W3C trace propagation).
3. Given the publish fails (transient or permanent), When the exception bubbles, Then the producer span is set to `ActivityStatusCode.Error` with the exception's message and `error.type` attribute.
4. Given a message goes through the outbox, When the outbox dispatcher publishes it later, Then the dispatcher's `NimBus.Outbox.Dispatch` span is the parent of the eventual `NimBus.Process` span — and crucially, the *original* HTTP-request span that wrote to the outbox is captured as the dispatcher span's `Link`, not its parent (causal-but-not-temporal relationship).

---

### User Story 3 — `messaging.*` semantic-convention tags (Priority: P1)

As an operator running NimBus alongside other instrumented services, I want NimBus span and metric attributes to follow the OpenTelemetry [`messaging.*` semantic conventions](https://opentelemetry.io/docs/specs/semconv/messaging/) so that NimBus traces interleave correctly with HTTP server / client traces, and so that messaging-aware observability backends (Honeycomb, Datadog APM, Application Insights) can render NimBus spans natively.

Why this priority: Today's tag set uses `messaging.event_type`, `messaging.destination` (not `messaging.destination.name`), and `messaging.message_id` (not `messaging.message.id`) — close enough to look right but not what backends key off. Aligning is a one-time mechanical change that unlocks built-in dashboards.

Independent Test: Send and consume a message with the new instrumentation. Capture the OTLP traces. Verify the attributes match the table in §*Span Attributes*. Run the same trace through an OTel-conformant backend (e.g., Aspire dashboard) and verify the message-flow visualization renders without manual mapping.

Acceptance Scenarios:

1. Given a `NimBus.Process` span, When it is exported, Then it has `messaging.system`, `messaging.operation.type` (`receive` or `process`), `messaging.destination.name`, `messaging.message.id`, `messaging.message.conversation_id`, and `messaging.message.body.size`. `messaging.destination.kind` is not emitted.
2. Given a `NimBus.Publish` span, When it is exported, Then it has `messaging.operation.type` = `publish`, plus the same set as above.
3. Given Service Bus is the transport, Then `messaging.system` = `servicebus`. Given RabbitMQ is the transport, Then `messaging.system` = `rabbitmq`. Given the in-memory transport is used, Then `messaging.system` = `nimbus.inmemory`.
4. Given the message has a `[SessionKey]` value, Then `messaging.servicebus.message.session.id` (Service Bus only) and the NimBus-specific `nimbus.session.key` (transport-agnostic) are both set.
5. Given a NimBus-specific concept that is not in the OTel spec (e.g., `event_type_id`, `permanent_failure`), Then it is exposed under the `nimbus.*` attribute namespace, never under `messaging.*` (which is reserved for the OTel spec).

---

### User Story 4 — Outbox, deferred, and resolver instrumentation (Priority: P1)

As an operator triaging "messages stuck somewhere", I want first-class instrumentation on the *internal* NimBus components — the outbox dispatcher, the deferred-message processor, and the resolver — so that the same set of dashboards answers "is the queue draining?" and "is the resolver writing?" without having to read NimBus source code.

Why this priority: The outbox, deferred processor, and resolver are NimBus's three biggest differentiators (ADR-002, ADR-003, ADR-005). They are also the three components most likely to silently fall behind. Instrumentation makes that visible.

Independent Test: Trigger each of the four scenarios — outbox backlog growth, deferred-message replay, resolver write, blocked-session unblock — and verify each produces the documented metric movements and trace spans.

Acceptance Scenarios:

1. Given a message is enqueued in the outbox, When `OutboxDispatcher` picks it up and publishes it, Then `nimbus.outbox.enqueued`, `nimbus.outbox.dispatched`, `nimbus.outbox.dispatch_lag` (gauge: `now - enqueued_at` for the oldest pending row) and a `NimBus.Outbox.Dispatch` span are emitted.
2. Given a session becomes blocked, When subsequent messages for that session are parked, Then `nimbus.deferred.parked` increments, `nimbus.deferred.pending` (gauge) reflects the parked count per session-key, and a `NimBus.DeferredProcessor.Park` span is emitted.
3. Given the session unblocks, When parked messages are replayed, Then `nimbus.deferred.replayed` increments, `nimbus.deferred.replay_duration` records the replay batch duration, and a `NimBus.DeferredProcessor.Replay` span is emitted (one span per batch, with `nimbus.deferred.batch_size` attribute).
4. Given the resolver writes a `MessageEntity` row, Then `nimbus.resolver.outcome_written` (counter, with `outcome` attribute = `resolved | dead_lettered | skipped | resubmitted`) increments and a `NimBus.Resolver.RecordOutcome` span is emitted.
5. Given the resolver writes a `MessageAuditEntity` row, Then `nimbus.resolver.audit_written` increments tagged with `audit_type`.
6. Given the message store falls behind (e.g., write latency > 1s p99), Then `nimbus.store.write_duration` reflects it via a histogram and an alert can be wired off the histogram percentile.

---

### User Story 5 — Activity-based handler instrumentation (Priority: P2)

As a developer writing a custom pipeline behavior, I want a stable `Activity` reference for the in-flight message that I can add tags / events to, so that my domain-specific telemetry (e.g., "tenant tier", "feature-flag bucket") shows up alongside the NimBus core span instead of in a sibling span.

Why this priority: Today there is no documented way to enrich the NimBus span; behaviors that try `Activity.Current` get the span, but the behavior is undocumented and could change. Making it a contract is cheap and prevents accidental breakage.

Independent Test: Write a custom `IMessagePipelineBehavior` that adds a `tenant.id` tag to `Activity.Current` and a custom event. Run a message through the pipeline. Verify the tag and event appear on the same span as the NimBus framework attributes.

Acceptance Scenarios:

1. Given a custom pipeline behavior runs inside the pipeline, When it reads `Activity.Current`, Then it gets the framework-managed `NimBus.Process` activity (not null, not a parent, not a child).
2. Given a behavior calls `Activity.Current?.SetTag("tenant.id", "...")`, When the activity ends, Then the tag is exported alongside the framework tags.
3. Given a behavior calls `Activity.Current?.AddEvent(new ActivityEvent(...))`, When the activity ends, Then the event is exported.
4. Given `IMessagePipelineBehavior.Handle` throws, When the activity ends, Then `ActivityStatusCode.Error` is set, the exception is recorded as an `ActivityEvent` (`exception` per OTel spec), and `error.type` is set to the exception type's full name.

---

### User Story 6 — Optional verbose-mode instrumentation (Priority: P3)

As a NimBus contributor debugging a specific incident, I want an optional verbose mode that emits per-step spans (`NimBus.Pipeline.Validate`, `NimBus.Pipeline.Authorize`, `NimBus.Resolver.Compose`, `NimBus.Store.Lookup`), so that I can see internal step timing without modifying production code paths.

Why this priority: Per-step spans are too noisy for production but invaluable for incident-triage. Off-by-default, on by configuration.

Independent Test: Set `NimBus__Otel__Verbose=true`. Run a message through the pipeline. Verify that the documented per-step spans appear under the `NimBus.Process` span. Disable verbose mode; verify the per-step spans are not emitted.

Acceptance Scenarios:

1. Given `NimBusOpenTelemetryOptions.Verbose = false` (default), When messages flow, Then only the parent-level spans (`NimBus.Publish`, `NimBus.Process`, `NimBus.Outbox.Dispatch`, etc.) are emitted.
2. Given `NimBusOpenTelemetryOptions.Verbose = true`, When messages flow, Then per-step child spans for pipeline behaviors and store operations are emitted under their parents.
3. Given verbose mode is on, Then no PII or message body content is added to span attributes (still bound by the *no-message-body* rule below).
4. Given a pipeline behavior is registered, When the verbose-mode wrapper runs, Then the per-behavior span is named `NimBus.Pipeline.{BehaviorTypeName}` (e.g., `NimBus.Pipeline.LoggingMiddleware`).

---

### User Story 7 — Trace propagation across both transports identically (Priority: P2)

As an operator running a hybrid deployment (Service Bus in production, RabbitMQ in dev), I want trace context to propagate identically — same header / property name, same wire format, same attributes — so that traces from the dev environment look the same as from production.

Why this priority: ADR-011 / spec 003 already commits to identical OTel behaviour across transports (NFR-012). This spec owns the contract and the tests that prove it.

Independent Test: With the in-memory transport, run a publish-then-consume round-trip. With the Service Bus transport, run the same. Compare the resulting trace JSON. The only attributes that differ are `messaging.system` and the transport-specific message-id values.

Acceptance Scenarios:

1. Given any transport, When a message is published, Then the W3C `traceparent` header / property is set on the outgoing message via the broker's native header mechanism (Service Bus `ApplicationProperties`, RabbitMQ basic-properties headers).
2. Given any transport, When a message is consumed, Then the receiver extracts `traceparent` from the same header / property and starts the consumer activity with that as the parent.
3. Given the W3C `tracestate` header is present, Then it is propagated through the broker untouched.
4. Given the transport assigns a native message id, Then it is exposed under `messaging.message.id`. The transport-specific id (e.g., `messaging.servicebus.message.id`) MAY also be set.
5. Given a message lacks a `traceparent`, When it is consumed, Then the consumer span is a root span (no fabricated parent) and `nimbus.message.has_parent_trace = false` is set.

---

### User Story 8 — `ServiceDefaults` migrated to use the new package (Priority: P2)

As a NimBus repo maintainer, I want `NimBus.ServiceDefaults.Extensions.ConfigureOpenTelemetry` to call `AddNimBusInstrumentation()` instead of re-implementing meter / source registration inline, so that the canonical wiring is in one place and cannot drift again.

Why this priority: `NimBus.ServiceDefaults/Extensions.cs:50` registers `"NimBus.ServiceBus"` and the `"NimBus"` source but not the `"NimBus.Pipeline"` meter. The drift demonstrates the bug class. The fix is to take a dependency on the new package.

Independent Test: Compare the meter / source list registered by `NimBus.ServiceDefaults` before and after this feature. After: zero hard-coded meter or source names — all inherited via `AddNimBusInstrumentation()`.

Acceptance Scenarios:

1. Given `NimBus.ServiceDefaults` is referenced, When the host starts, Then `NimBus.OpenTelemetry` is added transitively and `AddNimBusInstrumentation()` is called.
2. Given a host references `NimBus.ServiceDefaults` and *also* explicitly calls `AddNimBusInstrumentation()`, When metrics / traces are exported, Then no instruments are double-registered (the call is idempotent).
3. Given a host references `NimBus.OpenTelemetry` *without* `NimBus.ServiceDefaults`, When the host configures OTel manually, Then `AddNimBusInstrumentation()` is the only NimBus-specific call required.

---

## Edge Cases

- **High-cardinality attributes**. Naive use of `messaging.message.id` or `nimbus.session.key` as a metric attribute would explode metric cardinality. Counters and histograms MUST NOT include `messaging.message.id`, `nimbus.session.key`, or `messaging.message.conversation_id` as attributes. They are span-only attributes. Metrics are tagged with bounded-cardinality attributes only: `messaging.system`, `messaging.destination.name`, `messaging.operation.type`, `nimbus.event_type`, `nimbus.outcome`.
- **Message body in span attributes**. The instrumentation MUST NOT serialize the message body into span attributes or events. `messaging.message.body.size` (length in bytes) is the only body-related attribute. Body capture is out of scope (privacy, log volume, schema drift).
- **PII in event-type ids**. Event type ids (e.g., `crm.account.created.v1`) are considered low-PII by convention. The instrumentation does not redact them. Documentation MUST warn operators to avoid encoding PII into event-type ids.
- **Sampling and parent-based decisions**. The instrumentation does not configure samplers — that is the host's choice. Documentation MUST recommend `ParentBased(TraceIdRatioBased(...))` so that publish-side sampling decisions flow to the consumer.
- **Span end ordering**. The consumer span MUST end *after* the message is settled (Complete / DeadLetter / Defer), not after the handler returns, so that broker-side acknowledgement latency is included in the span duration.
- **Failed publishes that are retried by the outbox**. A publish failure that lands in the outbox MUST emit a publisher span with `error.type` set, then the eventual successful dispatch emits a *separate* `NimBus.Outbox.Dispatch` span linked to the original. They are not merged into one span.
- **In-memory transport**. The in-memory transport MUST emit identical instrumentation to the broker transports. Test fixtures rely on this.
- **Nested handler invocations**. If a handler publishes a downstream message, the new publish span MUST be a child of the in-flight `NimBus.Process` span (standard `Activity.Current` parenting).
- **Activity disabled at the source**. If the host adds `Filter` callbacks that exclude the NimBus source, instrumentation MUST be silent (no exceptions, no log spam, no fallback paths). The `ActivitySource.HasListeners()` check covers this.
- **Resolver writes from the receiver process vs. the dedicated Resolver function**. The resolver instrumentation MUST emit identical metrics regardless of where `ResolverService` runs — receiver-process inline mode and dedicated-Function mode produce the same metric series, distinguishable only by the `service.name` resource attribute.
- **OTel SDK version compatibility**. The package targets the OpenTelemetry SDK 1.x major series at the time of release. Breaking changes in OTel SDK 2.x require a major-version bump of `NimBus.OpenTelemetry`, not silent absorption.
- **Non-.NET-Aspire hosts**. The package MUST work in any host that uses `IHostBuilder` / `IHostApplicationBuilder`, not just Aspire. No Aspire-specific dependencies in `NimBus.OpenTelemetry`.
- **Azure Functions isolated worker**. The `NimBus.Resolver` Azure Function MUST be supported. The Function-specific OTel pipeline (`UseFunctionsWorkerDefaults`, `Microsoft.Azure.Functions.Worker.OpenTelemetry`) MUST coexist — `AddNimBusInstrumentation` is additive and does not reset the Functions OTel registration.
- **Activity link size limits**. Outbox-link scenarios attach the original write-time activity as an `ActivityLink`. If the link list grows beyond 32 entries (configurable), older links are dropped silently rather than throwing.
- **OpenTelemetry-via-`ConfigureOpenTelemetry` race with `NimBus.SDK` registration order**. `AddNimBusInstrumentation` MUST be order-independent: it can be called before or after `AddNimBusPublisher` / `AddNimBusSubscriber`. The hosted services that own the instruments MUST not assume registration order.

## Requirements

### Functional Requirements

#### Package and registration

The package exposes **three** entry points, each with a different ownership scope:

| Extension method | Owns |
|---|---|
| `IServiceCollection.AddNimBusInstrumentation(Action<NimBusOpenTelemetryOptions>?)` | Options binding, decorators (`InstrumentingSenderDecorator`, `InstrumentingMessageTrackingStoreDecorator`), `NimBusGaugeBackgroundService` hosted service. |
| `MeterProviderBuilder.AddNimBusInstrumentation()` | `AddMeter(...)` calls only. |
| `TracerProviderBuilder.AddNimBusInstrumentation()` | `AddSource(...)` calls only. |

Provider-builder extensions are deliberately narrow: they cannot register `IOptions<>`, decorators, or hosted services because the OpenTelemetry SDK's `Meter`/`TracerProviderBuilder` does not expose `IServiceCollection`. Splitting the registration is the only way to deliver the runtime components (gauge poller, decorators) cleanly.

- **FR-001**: NimBus MUST ship a new NuGet package `NimBus.OpenTelemetry` containing the three extension methods above and a public options class `NimBusOpenTelemetryOptions`.
- **FR-002**: The package MUST depend on `OpenTelemetry.Api`, `OpenTelemetry.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection.Abstractions`, and `Microsoft.Extensions.Hosting.Abstractions`. It MUST NOT depend on the OpenTelemetry SDK or any exporter package.
- **FR-003**: `IServiceCollection.AddNimBusInstrumentation(Action<NimBusOpenTelemetryOptions>?)` MUST register `IOptions<NimBusOpenTelemetryOptions>` (bound to the `NimBus:Otel` configuration section if present), the `InstrumentingSenderDecorator` (FR-050), the `InstrumentingMessageTrackingStoreDecorator` (FR-055), and the `NimBusGaugeBackgroundService` hosted service (FR-044). It MUST be idempotent — calling it more than once MUST NOT double-register decorators, hosted services, or options.
- **FR-004**: `MeterProviderBuilder.AddNimBusInstrumentation()` MUST register every NimBus-emitted meter listed in §*Metrics* as a single bundle, by calling `AddMeter(...)` for each canonical name. It MUST be idempotent.
- **FR-005**: `TracerProviderBuilder.AddNimBusInstrumentation()` MUST register every NimBus-emitted activity source listed in §*Traces* as a single bundle, by calling `AddSource(...)` for each canonical name. It MUST be idempotent.
- **FR-006**: The provider-builder extensions MUST work even when `IServiceCollection.AddNimBusInstrumentation` has not been called (so a host that wires OTel without DI for some reason still gets the meters/sources registered). In that mode, no decorators or gauge service exist — gauge instruments produce no values, and publish/store metrics are not emitted because the decorators are absent. This degraded mode MUST be documented, not silent.
- **FR-007**: The set of meter and source names MUST be exposed as `public const string` fields on a static class `NimBusInstrumentation` (e.g., `NimBusInstrumentation.ConsumerMeterName`) so that hand-rolled OTel pipelines can opt in selectively without depending on `NimBus.OpenTelemetry`.
- **FR-008**: Documentation MUST show the canonical three-call wiring pattern:

  ```csharp
  builder.Services.AddNimBusInstrumentation(opts => opts.Verbose = false);
  builder.Services.AddOpenTelemetry()
      .WithMetrics(m => m.AddNimBusInstrumentation())
      .WithTracing(t => t.AddNimBusInstrumentation());
  ```

#### Activity sources and spans

- **FR-010**: NimBus MUST expose the following `ActivitySource` names, each with documented span shapes:
  - `NimBus.Publisher` — emits `NimBus.Publish` (`Producer` kind) and, in verbose mode, `NimBus.Publish.Serialize`.
  - `NimBus.Consumer` — emits `NimBus.Process` (`Consumer` kind), `NimBus.Process.Settle` (verbose), `NimBus.Process.Defer` (verbose).
  - `NimBus.Outbox` — emits `NimBus.Outbox.Enqueue` (`Internal`), `NimBus.Outbox.Dispatch` (`Producer`), `NimBus.Outbox.Cleanup` (`Internal`).
  - `NimBus.DeferredProcessor` — emits `NimBus.DeferredProcessor.Park` (`Internal`), `NimBus.DeferredProcessor.Replay` (`Internal`).
  - `NimBus.Resolver` — emits `NimBus.Resolver.RecordOutcome` (`Internal`), `NimBus.Resolver.RecordAudit` (`Internal`).
  - `NimBus.Store` — emits `NimBus.Store.{Operation}` for every `IMessageTrackingStore` operation (verbose only, off by default — non-verbose covers store via metrics alone).

- **FR-011**: The existing `NimBus.ServiceBus.NimBusDiagnostics.ActivitySource` (named `"NimBus"`) MUST be removed in favour of `NimBus.Consumer`. The `NimBusDiagnostics` static class itself is removed; the `Diagnostic-Id` constant moves to an internal helper in `NimBus.Core`. No alias, no `[Obsolete]` shim. The current public surface is small (only the `Source` field and `ActivitySourceName` constant) and there is no documented external consumer.

- **FR-012**: The `NimBus.Process` span lifetime MUST cover handler dispatch *and* broker settle. The span ends after `Complete` / `Abandon` / `DeadLetter` / `Defer` returns. Today the span ends in `ServiceBusAdapter.HandleWithLatencyTracking` when the handler returns; this changes.

- **FR-013**: The `NimBus.Publish` span MUST be opened by an `ISender` decorator that wraps the transport-provided sender. The decorator lives in `NimBus.Core` (transport-agnostic) and is registered automatically by `AddNimBusPublisher` / `AddNimBusSubscriber`.

- **FR-014**: `OutboxSender` MUST emit a `NimBus.Outbox.Enqueue` span when writing a row, and `OutboxDispatcher` MUST emit a `NimBus.Outbox.Dispatch` span when reading and forwarding. The `Dispatch` span MUST attach the `Enqueue` span (or, more precisely, the `Activity` that was current at enqueue time) as an `ActivityLink`, so the trace shows causality without making the dispatcher span temporally nested under a long-since-closed HTTP request.

- **FR-015**: **Messaging spans** MUST follow the OTel-recommended span-name pattern `{messaging.operation.type} {messaging.destination.name}` (e.g., `publish my-queue`, `process my-queue`, `settle my-queue`). Span-name cardinality is bounded by the deployment's destination count, which is already a low-cardinality dimension. **Internal-only spans** that do not represent a messaging operation (`NimBus.Resolver.RecordOutcome`, `NimBus.Resolver.RecordAudit`, `NimBus.DeferredProcessor.Park`, `NimBus.DeferredProcessor.Replay`, `NimBus.Outbox.Cleanup`, `NimBus.Store.{Operation}`) MUST use the stable identifier as the span name (no destination embedded). The classification of each span is in FR-010.

#### Span attributes (semantic conventions)

The canonical reference is the OpenTelemetry messaging semantic conventions ([general spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/), [Azure messaging](https://opentelemetry.io/docs/specs/semconv/messaging/azure-messaging/)) at version **1.41** or later — whichever is the latest stable version at the time this feature ships. Attribute names below match 1.41.

- **FR-020**: All messaging spans MUST set the following OTel `messaging.*` attributes:
  - `messaging.system` — required values: `servicebus` (Azure Service Bus), `rabbitmq` (RabbitMQ), `nimbus.inmemory` (in-memory transport — the `nimbus.*` system value is permitted by the OTel spec for non-standardized systems).
  - `messaging.operation.type` — required values: `publish`, `receive`, `process`, `settle`. This is the OTel-defined *operation type* taxonomy.
  - `messaging.operation.name` (optional) — system-specific operation name when distinct from the type (e.g., `complete`, `abandon`, `defer` for settle operations on Service Bus).
  - `messaging.destination.name` — queue or topic name.
  - `messaging.destination.template` (optional) — when the destination is templated.
  - `messaging.message.id` — broker-assigned id.
  - `messaging.message.conversation_id` — NimBus conversation id; falls back to message id when absent.
  - `messaging.message.body.size` — raw broker-side body size in bytes.

  `messaging.destination.kind` is **not** emitted. The OTel spec deprecated it; backends infer kind from `messaging.system` plus the destination name shape.

- **FR-021**: NimBus-specific attributes that have no OTel equivalent MUST be exposed under the `nimbus.*` namespace:
  - `nimbus.event_type` (event type id)
  - `nimbus.session.key` (session key — *spans only*, never on metrics)
  - `nimbus.outcome` (`completed`, `dead_lettered`, `deferred`, `abandoned`, `permanent_failure`, `transient_failure`)
  - `nimbus.permanent_failure` (bool)
  - `nimbus.delivery_count` (broker-reported delivery count)
  - `nimbus.handler.type` (FQTN of the `IEventHandler<T>` invoked)
  - `nimbus.endpoint` (logical endpoint name from `PlatformConfiguration`)
  - `nimbus.has_parent_trace` (bool — whether `traceparent` was extracted)

- **FR-022**: Errors MUST be recorded per OTel exception convention: `ActivityStatusCode.Error`, an `ActivityEvent` named `exception` with `exception.type`, `exception.message`, `exception.stacktrace` attributes, and `error.type` set on the span.

- **FR-023**: Transport-specific attribute namespaces from the OTel Azure-messaging and RabbitMQ semconv MAY be set in addition to the generic ones. They MUST NOT replace the generic ones. Examples:
  - Service Bus: `messaging.servicebus.message.delivery_count`, `messaging.servicebus.message.enqueued_time`, `messaging.servicebus.destination.subscription_name`.
  - RabbitMQ: `messaging.rabbitmq.destination.routing_key`, `messaging.rabbitmq.message.delivery_tag`.

- **FR-024**: The `[SessionKey]` attribute value, when present, MUST be exposed as:
  - `nimbus.session.key` — canonical NimBus attribute, transport-agnostic.
  - `messaging.servicebus.message.session.id` — Service Bus-specific (per OTel Azure-messaging semconv).

  It MUST NOT be exposed via `messaging.message.id`, which is the broker-assigned message id, not the application's session key.

- **FR-025**: When the OpenTelemetry messaging semconv version pinned in this spec (1.41) ships incompatible changes in a future stable revision, the upgrade is a breaking change and follows the same release-notes process as FR-104. The package MUST NOT silently dual-emit old and new attribute names.

#### Trace propagation

- **FR-030**: The W3C `traceparent` header MUST be the canonical propagation format. NimBus MUST NOT write or read the legacy `Diagnostic-Id` property in any code path. In-flight messages produced by older NimBus builds at upgrade time lose parent-trace linkage; this is the documented upgrade impact (FR-101).

- **FR-031**: Both transports MUST carry `traceparent` and `tracestate` via their native header / property mechanism. The Service Bus implementation lives in `MessageHelper`; the RabbitMQ implementation will live in the new `NimBus.Transport.RabbitMQ` package per spec 003. Both MUST use the same property name (`traceparent`, `tracestate`) — no transport-prefixed names.

- **FR-032**: Trace propagation MUST also work for messages sent through the outbox. The outbox row MUST persist `traceparent` and `tracestate`. The dispatcher MUST set them on the outgoing message, so that a publisher's HTTP-request trace flows through the outbox into the eventual consumer span. Concrete requirements:

  - **Model**: `OutboxMessage` (`src/NimBus.Core/Outbox/OutboxMessage.cs`) MUST gain `string? TraceParent` and `string? TraceState` properties. Today the row stores only `CorrelationId`, which is insufficient for W3C trace propagation.
  - **SQL Server schema**: the outbox table MUST gain two columns — `TraceParent NVARCHAR(55) NULL` (W3C traceparent header maximum length) and `TraceState NVARCHAR(256) NULL`. The schema-init code in `SqlServerOutbox` MUST add the columns; an idempotent `ALTER TABLE ... ADD ... IF NOT EXISTS` migration is required for existing deployments.
  - **Population**: `OutboxSender` MUST capture `Activity.Current?.Id` as `TraceParent` and `Activity.Current?.TraceStateString` as `TraceState` when writing the row.
  - **Restoration**: `OutboxDispatcher` MUST set the captured `traceparent` / `tracestate` on the outgoing message and create the dispatch span as a root span linked (via `ActivityLink`) to the original `traceparent`.
  - **Pre-existing rows without trace context**: rows persisted before the schema migration (where `TraceParent IS NULL`) MUST dispatch successfully — no exception, no startup failure. The resulting dispatch span has no link, and an `ActivityEvent` named `nimbus.outbox.orphan_row` is emitted at `INFO` level. The conformance suite MUST cover this case.

- **FR-033**: The instrumentation MUST be compatible with `Baggage` propagation. `Baggage` MUST NOT be inspected, copied into span attributes, or filtered — it is a transparent passthrough.

#### Metrics

- **FR-040**: Metrics MUST use the meter name `NimBus.{Component}` to align with .NET 8+ naming conventions. The complete meter list:
  - `NimBus.Publisher`
  - `NimBus.Consumer`
  - `NimBus.Pipeline` (already exists; may be folded under `NimBus.Consumer` — see Open Questions)
  - `NimBus.Outbox`
  - `NimBus.DeferredProcessor`
  - `NimBus.Resolver`
  - `NimBus.Store`

- **FR-041**: The complete instrument list, with type, unit, and bounded-cardinality attribute set, MUST be:

| Instrument | Meter | Type | Unit | Attributes |
|---|---|---|---|---|
| `nimbus.message.published` | `NimBus.Publisher` | Counter | `{messages}` | `messaging.system`, `messaging.destination.name`, `nimbus.event_type` |
| `nimbus.message.publish.duration` | `NimBus.Publisher` | Histogram | `ms` | same |
| `nimbus.message.publish.failed` | `NimBus.Publisher` | Counter | `{messages}` | same + `error.type` |
| `nimbus.message.received` | `NimBus.Consumer` | Counter | `{messages}` | `messaging.system`, `messaging.destination.name`, `nimbus.event_type` |
| `nimbus.message.processed` | `NimBus.Consumer` | Counter | `{messages}` | same + `nimbus.outcome` |
| `nimbus.message.process.duration` | `NimBus.Consumer` | Histogram | `ms` | same |
| `nimbus.message.queue_wait` | `NimBus.Consumer` | Histogram | `ms` | same (replaces existing `nimbus.message.queue_wait` on `NimBus.ServiceBus` meter — moves to transport-neutral meter) |
| `nimbus.message.e2e_latency` | `NimBus.Consumer` | Histogram | `ms` | same (relocated likewise) |
| `nimbus.outbox.enqueued` | `NimBus.Outbox` | Counter | `{messages}` | `nimbus.endpoint` |
| `nimbus.outbox.dispatched` | `NimBus.Outbox` | Counter | `{messages}` | `nimbus.endpoint` + `nimbus.outcome` |
| `nimbus.outbox.dispatch.duration` | `NimBus.Outbox` | Histogram | `ms` | same |
| `nimbus.outbox.pending` | `NimBus.Outbox` | Gauge | `{messages}` | `nimbus.endpoint` |
| `nimbus.outbox.dispatch_lag` | `NimBus.Outbox` | Gauge | `s` | `nimbus.endpoint` |
| `nimbus.deferred.parked` | `NimBus.DeferredProcessor` | Counter | `{messages}` | `nimbus.endpoint` |
| `nimbus.deferred.replayed` | `NimBus.DeferredProcessor` | Counter | `{messages}` | `nimbus.endpoint` |
| `nimbus.deferred.replay.duration` | `NimBus.DeferredProcessor` | Histogram | `ms` | same |
| `nimbus.deferred.pending` | `NimBus.DeferredProcessor` | Gauge | `{messages}` | `nimbus.endpoint` |
| `nimbus.deferred.blocked_sessions` | `NimBus.DeferredProcessor` | Gauge | `{sessions}` | `nimbus.endpoint` |
| `nimbus.resolver.outcome_written` | `NimBus.Resolver` | Counter | `{records}` | `nimbus.endpoint`, `nimbus.outcome` |
| `nimbus.resolver.audit_written` | `NimBus.Resolver` | Counter | `{records}` | `nimbus.endpoint`, `audit_type` |
| `nimbus.resolver.write.duration` | `NimBus.Resolver` | Histogram | `ms` | `nimbus.endpoint` |
| `nimbus.store.operation.duration` | `NimBus.Store` | Histogram | `ms` | `nimbus.store.operation`, `nimbus.store.provider` |
| `nimbus.store.operation.failed` | `NimBus.Store` | Counter | `{ops}` | same + `error.type` |

- **FR-042**: The two existing meters `"NimBus.Pipeline"` (currently in `MetricsMiddleware.cs`) and `"NimBus.ServiceBus"` (currently in `ServiceBusAdapter.cs`) MUST be removed and replaced by `NimBus.Consumer`. The legacy meter names are not retained — there are no shipping NuGet consumers of `NimBus.OpenTelemetry`, no published dashboards keyed off the legacy names, and the in-tree drift in `NimBus.ServiceDefaults` already proves the old surface is not load-bearing.

- **FR-043**: All histogram instruments MUST use the OpenTelemetry recommended exponential-bucket histogram aggregation when consumed via the OTel SDK. The package MUST NOT hard-code bucket boundaries.

- **FR-044**: Gauge instruments (e.g., `nimbus.outbox.pending`, `nimbus.deferred.pending`) MUST be implemented as `ObservableGauge<long>` driven by polling the corresponding store. The poll interval is configurable via `NimBusOpenTelemetryOptions.GaugePollInterval` (default 30s).

- **FR-045**: Metric attributes MUST be bounded-cardinality only. The instrumentation MUST NOT emit `messaging.message.id`, `nimbus.session.key`, or `messaging.message.conversation_id` as metric attributes.

#### Component instrumentation

- **FR-050**: An `ISender` decorator implementing publish-side instrumentation MUST be registered automatically by `AddNimBusPublisher` and `AddNimBusSubscriber`. The decorator wraps the transport-provided `ISender` and is conceptually parallel to the existing `OutboxSender`.

- **FR-051**: The existing `MetricsMiddleware` MUST be updated to emit instruments under the new meter names per FR-040 / FR-042 with attributes per FR-045.

- **FR-052**: `OutboxDispatcher` (`src/NimBus.Core/Outbox/OutboxDispatcher.cs`) MUST emit `NimBus.Outbox.Dispatch` spans and the `nimbus.outbox.*` instruments per FR-041. The current `IOutbox` (`src/NimBus.Core/Outbox/IOutbox.cs`) only exposes `GetPendingAsync(batchSize)` plus mark/store methods, which cannot produce exact `pending` count or oldest-row `dispatch_lag` without abusing batched reads. This feature MUST add a query contract:

  ```csharp
  public interface IOutboxMetricsQuery
  {
      Task<long> GetPendingCountAsync(CancellationToken ct);
      Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken ct);
  }
  ```

  The contract MAY live as a separate interface (preferred — keeps `IOutbox` focused on the dispatch path) or as default-interface methods on `IOutbox`. `SqlServerOutbox` MUST implement it via single-statement queries (`SELECT COUNT(*)` filtered by status; `SELECT TOP 1 EnqueuedAtUtc ... ORDER BY EnqueuedAtUtc ASC`). When no provider implements `IOutboxMetricsQuery`, `NimBusGaugeBackgroundService` MUST emit no values for the corresponding gauges (`nimbus.outbox.pending`, `nimbus.outbox.dispatch_lag`) and log a one-time INFO message — it MUST NOT fall back to scanning via `GetPendingAsync(batchSize)`.

- **FR-053**: `DeferredMessageProcessor` (`src/NimBus.ServiceBus/DeferredMessageProcessor.cs` today; eventually moved to `NimBus.Core` per spec 003 FR-050) MUST emit the `nimbus.deferred.*` instruments and corresponding spans.

- **FR-054**: `ResolverService` (`src/NimBus.Resolver/Services/ResolverService.cs`) MUST emit the `nimbus.resolver.*` instruments and `NimBus.Resolver.*` spans for `RecordOutcome` and `RecordAudit` calls.

- **FR-055**: An `IMessageTrackingStore` decorator MUST be registered automatically by storage-provider extensions to emit `nimbus.store.*` instruments. The decorator dispatches to the inner store and times each operation; in verbose mode it also emits per-operation spans. Both Cosmos DB and SQL Server providers MUST be wrapped identically — the metric series differs only in the `nimbus.store.provider` attribute (`cosmos` or `sqlserver`).

- **FR-056**: The `NimBus.WebApp` and `NimBus.Resolver` host projects MUST take a dependency on `NimBus.OpenTelemetry` and use the canonical three-call wiring pattern from FR-008: `Services.AddNimBusInstrumentation(...)`, `WithMetrics(m => m.AddNimBusInstrumentation())`, `WithTracing(t => t.AddNimBusInstrumentation())`.

#### Service Defaults integration

- **FR-060**: `NimBus.ServiceDefaults.Extensions.ConfigureOpenTelemetry` MUST be rewritten to use the canonical three-call wiring pattern from FR-008. The hard-coded `AddMeter("NimBus.ServiceBus")` and `AddSource("NimBus")` lines are removed.

- **FR-061**: The hard-coded source name `"Azure.Messaging.ServiceBus"` in `ServiceDefaults` (line 57) MAY remain as it is the Azure SDK's source, not NimBus's. It is unaffected by this feature.

- **FR-062**: After this feature, `NimBus.ServiceDefaults` MUST contain zero hard-coded NimBus meter or source string literals.

#### Configuration

- **FR-070**: `NimBusOpenTelemetryOptions` MUST expose at least:
  - `Verbose` (bool, default `false`) — enable per-step spans (User Story 6).
  - `IncludeMessageHeaders` (bool, default `false`) — set selected NimBus headers as span events (still no body content).
  - `GaugePollInterval` (TimeSpan, default 30s).
  - `OutboxLagWarnThreshold` (TimeSpan? — when set and `dispatch_lag` gauge exceeds it, an `ActivityEvent` is emitted).

- **FR-071**: Options MUST be configurable via `IConfiguration` under the `NimBus:Otel` section (e.g., `NimBus__Otel__Verbose=true`).

#### Testing

- **FR-080**: A new test project `tests/NimBus.OpenTelemetry.Tests/` MUST host instrumentation conformance tests using `MeterProviderBuilder.SetExporter(new InMemoryExporter<Metric>(...))` and `TracerProviderBuilder.AddInMemoryExporter(...)`.

- **FR-081**: The test suite MUST verify, for every `IEventHandler<T>` invocation in the in-memory transport: a `NimBus.Process` span is emitted with the documented attribute set, and `nimbus.message.received` / `nimbus.message.processed` / `nimbus.message.process.duration` are recorded with the documented attribute set.

- **FR-082**: The test suite MUST verify trace propagation: a synthetic publish→consume round-trip produces a single trace id with a publish span as the parent of the consume span.

- **FR-083**: The test suite MUST verify error attribution: a handler throw produces `ActivityStatusCode.Error`, an `exception` event, and `error.type` on the span.

- **FR-084**: The test suite MUST verify cardinality: no metric tag value contains a per-message identifier (asserted by inspecting the emitted attribute keys against a deny-list).

- **FR-085**: The transport conformance suite from spec 003 (`NimBus.Testing.Conformance`) MUST gain an *Instrumentation* category that runs against any registered transport and asserts identical span / metric output across transports.

#### Documentation

- **FR-090**: NimBus MUST document the package, the registration entry point, the meter / source list, the `messaging.*` attribute mapping, the `nimbus.*` attribute namespace, the verbose-mode contract, and the W3C propagation guarantee. Page: `docs/observability.md` (new). Existing observability content scattered across `docs/architecture.md` MUST be cross-linked.

- **FR-091**: The page MUST include sample dashboards (Aspire dashboard JSON, Grafana JSON) for the four hot dashboards: *Publisher health*, *Consumer health*, *Outbox lag*, *Resolver throughput*.

- **FR-092**: The upgrade impact MUST be documented step-by-step in `docs/observability.md` and the release notes: the meter / source / property / attribute renames, the dashboard search-and-replace recipe, and the warning that in-flight messages produced by an older NimBus build lose parent-trace linkage during the upgrade window.

#### Breaking changes

- **FR-100**: This feature is a breaking change to the instrumentation surface. The legacy meter names (`NimBus.ServiceBus`, `NimBus.Pipeline`), the legacy activity source (`NimBus`), the legacy property name (`Diagnostic-Id`), and the legacy attribute names (`messaging.destination`, `messaging.event_type`, `messaging.message_id`) are all removed. There are no aliases, no `[Obsolete]` shims, and no transitional dual-emission.

- **FR-101**: The `Diagnostic-Id` Service Bus application property is removed from both write and read paths. NimBus emits and consumes `traceparent` / `tracestate` only. In-flight messages produced by older NimBus builds at the time of upgrade lose their parent trace linkage; this is documented as the upgrade impact.

- **FR-102**: `NimBus.ServiceBus.NimBusDiagnostics` (public static class with `Source` field, `ActivitySourceName` constant, `DiagnosticIdProperty` constant) is deleted. The `Diagnostic-Id` literal moves to an internal helper in `NimBus.Core`; the activity source moves to `NimBus.OpenTelemetry`. Direct external consumers of `NimBusDiagnostics` are not expected — it has never been documented as a public extension point.

- **FR-103**: `MetricsMiddleware` retains its public type and constructor; only the internal meter / instrument names change. Pipeline-behavior consumers that take a dependency on the type (none today) are not affected.

- **FR-104**: The breaking change MUST be documented in `docs/observability.md` and in the release notes for the version that ships this feature. The release notes include a one-paragraph upgrade impact statement and a worked example of the dashboard rename (PromQL/KQL search-and-replace).

### Non-Functional Requirements

- **NFR-001**: The instrumentation overhead per-message at the 99th percentile MUST be ≤ 5% of the median handler runtime when `Verbose = false`. Measured by running the conformance suite with and without the OTel SDK attached.
- **NFR-002**: `AddNimBusInstrumentation` MUST be allocation-light at host startup — under 1ms of CPU and zero per-message allocations from the registration code path.
- **NFR-003**: The package MUST not introduce an exporter dependency. Exporter choice is the host's responsibility.
- **NFR-004**: The package MUST target `net10.0` (matching the rest of the repo) and follow the existing analyzer settings (StyleCop, Meziantou, SonarAnalyzer) without new exemptions.
- **NFR-005**: Public types in the package MUST have XML doc comments. The package MUST satisfy the existing `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` settings under Release configuration.
- **NFR-006**: Span and metric attribute keys MUST be string-interned via `static readonly` constants — no `$"..."` interpolation in hot paths.
- **NFR-007**: The package MUST be safe to call from any host: console, ASP.NET Core, Azure Functions isolated worker, Aspire AppHost reference projects.
- **NFR-008**: The package MUST not log or trace anything itself (no `ILogger` dependency from inside instrumentation code paths). Instrumentation that emits a span / metric MUST not also emit a log message — that decision belongs to consumers via OTel logging exporters.
- **NFR-009**: Verbose-mode spans MUST carry the same `traceparent` and remain children of the framework parent, so that turning verbose on does not change trace topology — only adds detail.

## Key Entities

- **`NimBus.OpenTelemetry`** — new NuGet package; thin wrapper that registers meters, sources, and the gauge poller.
- **`NimBusInstrumentation`** — public static class exposing the canonical meter / source name constants.
- **`NimBusOpenTelemetryOptions`** — public options class (`Verbose`, `IncludeMessageHeaders`, `GaugePollInterval`, `OutboxLagWarnThreshold`).
- **`InstrumentingSenderDecorator`** — wraps `ISender` to emit the publish span and publish metrics.
- **`InstrumentingMessageTrackingStoreDecorator`** — wraps `IMessageTrackingStore` to emit store metrics.
- **`NimBusGaugeBackgroundService`** — hosted service that polls outbox, deferred-processor, and store backends to populate observable gauges.
- **`NimBusActivitySources`** — internal static holder of the `ActivitySource` instances.
- **`NimBusMeters`** — internal static holder of the `Meter` instances.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A developer can enable full NimBus instrumentation in a host with the three-call wiring pattern from FR-008: one `Services.AddNimBusInstrumentation(...)`, one `WithMetrics(m => m.AddNimBusInstrumentation())`, one `WithTracing(t => t.AddNimBusInstrumentation())`. No NimBus meter, source, or attribute name appears in the host's source code.
- **SC-002**: A publish→consume round-trip in any sample produces a single distributed trace (one `traceparent`) with a publisher `Producer` span as the parent of a consumer `Consumer` span. The trace is rendered correctly in Aspire dashboard, Application Insights, and any OTel-conformant backend without manual mapping.
- **SC-003**: Every NimBus component listed in §*Span Attributes* / §*Metrics* (publisher, consumer, outbox, deferred processor, resolver, store) emits at least one span and at least one metric per its operation. Verified by the conformance suite (FR-080..FR-085).
- **SC-004**: All NimBus span and metric attribute names match the OpenTelemetry messaging semantic conventions ([general spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/), [Azure messaging](https://opentelemetry.io/docs/specs/semconv/messaging/azure-messaging/)) version **1.41** or the latest stable at release time — including `messaging.system = servicebus` (not `azureservicebus`), `messaging.operation.type` (not `messaging.operation`), no `messaging.destination.kind`, and `messaging.servicebus.*` (not `messaging.azureservicebus.*`). Span names follow the `{operation.type} {destination.name}` pattern for messaging spans (FR-015). NimBus-specific attributes use the `nimbus.*` namespace.
- **SC-005**: `NimBus.ServiceDefaults` contains zero hard-coded NimBus meter / source names after this feature lands. Verified by `grep -E '"NimBus(\.[A-Za-z]+)*"'` returning no matches in `src/NimBus.ServiceDefaults/`.
- **SC-006**: The legacy meter names (`NimBus.ServiceBus`, `NimBus.Pipeline`), the legacy activity source (`NimBus`), and the legacy `Diagnostic-Id` property name are absent from the codebase after this feature lands. Verified by repository-wide `grep` returning no matches outside the migration documentation.
- **SC-007**: Per-message instrumentation overhead is ≤ 5% of the median handler runtime at the 99th percentile when `Verbose = false`. Benchmarked via the conformance suite with `BenchmarkDotNet`.
- **SC-008**: Every metric attribute set is bounded — no per-message identifier ever lands on a metric series. Verified by FR-084.
- **SC-009**: The transport conformance suite *Instrumentation* category passes for both the in-memory transport and the Service Bus transport. After spec 003 lands, RabbitMQ joins this set with no source changes to `NimBus.OpenTelemetry`.
- **SC-010**: The W3C propagation contract is verified in CI: a publish through `OutboxSender` followed by a delayed dispatch produces a single trace id; the original HTTP-request span is captured as an `ActivityLink` on the dispatcher span.

## Assumptions

- The OpenTelemetry SDK 1.x is the supported runtime for consumers. Hosts on older OTel versions (0.x) are unsupported.
- `messaging.*` semantic conventions at version 1.41 are stable enough to commit to. Future incompatible revisions are handled per FR-025.
- Exponential bucket histograms are available in the OTel SDK consumers use. (Available since `OpenTelemetry.Metrics` 1.5.)
- Hosts use `IHostBuilder` / `IHostApplicationBuilder` for OTel registration. The package does not target alternative DI containers.
- Resource attributes (`service.name`, `service.namespace`, `service.version`, `deployment.environment`) are set by the host, not by `NimBus.OpenTelemetry`. Consumers configure them via OTel's own `ResourceBuilder`.
- The Cosmos DB and SQL Server provider packages will accept the `IMessageTrackingStore` decorator pattern (they already register the inner store via DI; the decorator slots in the same way `OutboxSender` decorates `ISender`).
- The W3C `traceparent` format will not be replaced by a non-backwards-compatible scheme during the support window of this feature.
- Operators want one set of NimBus instruments — not per-deployment custom instruments. Platform-level extensibility (e.g., adding `tenant.id` per-host) belongs in pipeline behaviors that enrich `Activity.Current`, not in the framework.

## Out of Scope

- Capturing message body content as span attributes or events (privacy / volume / schema-drift risk).
- A NimBus-shipped exporter (Application Insights, OTLP, Prometheus). Exporter choice is the host's.
- A custom NimBus sampler. Sampling configuration is the host's.
- Continuous-profiling / eBPF instrumentation. We instrument at the application layer only.
- Log correlation beyond the standard `TraceId` / `SpanId` injection that the OTel logging provider already does. We do not ship custom enrichers.
- Replacing the existing `Microsoft.Extensions.Logging` usage in handlers / middleware. ADR-006 stays as-is.
- A web UI for browsing NimBus traces / metrics. The WebApp's existing operational views are unaffected by this feature.
- Per-handler dashboards or auto-generated alerts. Operators wire those off the metric series themselves.
- Cross-process activity baggage propagation policies (read-only, allow-list, etc.). We pass `Baggage` through unchanged.
- A `NimBus.OpenTelemetry.Aspire` package. Aspire-specific wiring is handled by `NimBus.ServiceDefaults`.

## Open Questions

- **`NimBus.Pipeline` meter — fold in or keep separate?** The `NimBus.Pipeline` meter today emits `nimbus.pipeline.duration|processed|failed`. These are conceptually consumer-side metrics. Folding into `NimBus.Consumer` reduces meter count; keeping separate preserves the per-pipeline-step distinction. **Proposed**: fold into `NimBus.Consumer`. The legacy `NimBus.Pipeline` meter is deleted outright per FR-100.
- ~~**`messaging.destination.kind` retirement.**~~ **Resolved** (FR-020): not emitted. The OTel spec deprecated the attribute and this is a fresh package with no shipped consumers, so backend-compat does not apply.
- **`messaging.message.body.size` for Cosmos-stored messages.** The "body" the consumer sees is post-deserialization. Do we record raw broker-side bytes or post-deserialize size? **Proposed**: raw broker-side bytes, captured in the transport adapter.
- **Per-handler-type histograms vs single histogram with `nimbus.handler.type` tag.** A high-cardinality `handler.type` tag on a histogram is risky in some backends. **Proposed**: include the tag (it is bounded by the deployment's handler list, not by per-message data); document the cardinality risk.
- **Outbox-pending gauge implementation cost.** Polling the outbox table every 30s is cheap on SQL Server but non-trivial on a busy deployment. Should the gauge be a strict `count(*)` or an approximate `select top (1) pending_id desc` proxy? **Proposed**: exact `count(*)` with a configurable interval; document how to disable.
- **`NimBus.OpenTelemetry` versus `NimBus.Diagnostics` naming.** Flowly named theirs `Flowly.OpenTelemetry`. NimBus's existing static class is `NimBusDiagnostics`. The package name should mirror the user-facing role (OTel) rather than the internal name. **Proposed**: `NimBus.OpenTelemetry`.
- **Default for `Verbose`.** The verbose mode is high-value for debugging but high-volume in production. **Proposed**: off by default everywhere, including in samples.
- **Trace context on dead-letter resubmit.** When an operator clicks "resubmit" in the WebApp, does the resubmitted message carry the *original* trace context or start a fresh trace? Original loses the operator's intent; fresh loses the chain. **Proposed**: fresh trace, with an `ActivityLink` to the original outcome record's trace id.
- **Resolver instrumentation when running inline (non-Function) mode.** Inline mode dispatches resolver writes off the consumer thread. Do those writes get a child span under the `NimBus.Process` span, or a sibling span under a synthetic `NimBus.Resolver.Background` parent? **Proposed**: child span when inline; root span when standalone Function — `service.name` differentiates.

## Resolved Questions (from prior revision)

- The instrumentation surface is a single dedicated package, not split per-component (e.g., not `NimBus.OpenTelemetry.Outbox` + `NimBus.OpenTelemetry.Resolver`). One package, one extension method.
- Package depends on `OpenTelemetry.Api`, not the SDK. Exporter choice stays with the host.
- `messaging.*` semantic conventions are the canonical attribute schema. `nimbus.*` is the namespace for NimBus-specific attributes.
- W3C `traceparent` is the canonical propagation format. The legacy `Diagnostic-Id` Service Bus application property is removed from both write and read paths in this feature; in-flight messages produced by older NimBus builds at upgrade time lose parent-trace linkage.
- The publish path gets first-class instrumentation via an `ISender` decorator, not by adding instrumentation to each transport's `Sender` implementation.
- `IMessageTrackingStore` is instrumented by decorator, not by edits to each provider implementation.
- Verbose mode is opt-in via configuration, not a build-time switch.
- Both transports (Service Bus today, RabbitMQ via spec 003) emit identical instrumentation surface; only `messaging.system` differentiates.
- `NimBus.ServiceDefaults` migrates to call `AddNimBusInstrumentation()` and contains zero hard-coded meter / source names afterwards.
- The legacy meters (`NimBus.ServiceBus`, `NimBus.Pipeline`), the legacy activity source (`NimBus`), and the `NimBusDiagnostics` static class are deleted outright, not aliased. There are no shipped consumers and no published dashboards keyed off the old surface, so there is nothing to bridge.
- Conformance tests live in `NimBus.Testing.Conformance` and run against every transport via the existing harness.

## Phasing Reference

Delivered in three phases. Phase 1 is independently shippable; Phases 2 and 3 are additive.

| Phase | Scope | Decision Gate |
|---|---|---|
| 4.1 | New `NimBus.OpenTelemetry` package; `AddNimBusInstrumentation` extensions; consumer + publisher span migration; `messaging.*` attribute alignment; deletion of legacy `NimBusDiagnostics`, legacy meters, and `Diagnostic-Id`; `NimBus.ServiceDefaults` retrofit | **Yes** — review the public API surface; promote to stable only after one minor-version dogfooding |
| 4.2 | Outbox, deferred-processor, and resolver instrumentation; `IMessageTrackingStore` decorator; observable gauges; gauge background service | None |
| 4.3 | Verbose-mode per-step spans; `IncludeMessageHeaders` option; sample dashboards (Aspire / Grafana JSON in `docs/observability.md`); migration documentation | None |
