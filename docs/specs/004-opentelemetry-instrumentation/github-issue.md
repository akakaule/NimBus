# First-class OpenTelemetry instrumentation package (`NimBus.OpenTelemetry`)

> Spec: [`docs/specs/004-opentelemetry-instrumentation/spec.md`](spec.md)
> ADR: TBD (proposed: ADR-013)
> Tracking issue: TBD

## Summary

Today NimBus has *partial* instrumentation:

- One activity source `"NimBus"` declared in `src/NimBus.ServiceBus/NimBusDiagnostics.cs`, used only by `ServiceBusAdapter` to emit a single `NimBus.Process` consumer span.
- Two ad-hoc meters: `"NimBus.ServiceBus"` (`ServiceBusAdapter.cs:23` — `nimbus.message.e2e_latency`, `nimbus.message.queue_wait`) and `"NimBus.Pipeline"` (`MetricsMiddleware.cs:17` — `nimbus.pipeline.duration|processed|failed`).
- W3C trace propagation via the `Diagnostic-Id` Service Bus application property (the legacy name, not `traceparent`).
- `NimBus.ServiceDefaults/Extensions.cs:50` registers `"NimBus.ServiceBus"` and `"NimBus"` but **misses `"NimBus.Pipeline"`** — instrumentation drift already happened.
- No publish-side spans, no outbox / deferred / resolver / message-store instrumentation, no `messaging.*` semantic-convention compliance, no shipped NuGet for consumers.

This issue tracks the work to ship a single `NimBus.OpenTelemetry` package that:

1. Registers every NimBus meter / source with one extension call.
2. Instruments every component (publisher, consumer, outbox, deferred-message processor, resolver, message store) — not just the receive path.
3. Aligns attribute names with the OpenTelemetry [`messaging.*` semantic conventions](https://opentelemetry.io/docs/specs/semconv/messaging/), and reserves the `nimbus.*` namespace for NimBus-specific attributes.
4. Adopts W3C `traceparent` as the canonical propagation format; the legacy `Diagnostic-Id` property is removed from both write and read paths.
5. Migrates `NimBus.ServiceDefaults` to call `AddNimBusInstrumentation()` so the canonical wiring lives in one place.

## Package and registration (FR-001..FR-008)

New NuGet `NimBus.OpenTelemetry`. **Three** entry points, each with a different ownership scope:

| Extension method | Owns |
|---|---|
| `IServiceCollection.AddNimBusInstrumentation(Action<NimBusOpenTelemetryOptions>?)` | Options binding, decorators (sender + store), `NimBusGaugeBackgroundService` hosted service |
| `MeterProviderBuilder.AddNimBusInstrumentation()` | `AddMeter(...)` calls only |
| `TracerProviderBuilder.AddNimBusInstrumentation()` | `AddSource(...)` calls only |

Splitting is required because `MeterProviderBuilder` / `TracerProviderBuilder` cannot register `IOptions<>`, decorators, or hosted services. The DI service collection is the only ownership point that can.

Canonical wiring (FR-008):

```csharp
builder.Services.AddNimBusInstrumentation(opts => opts.Verbose = false);
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddNimBusInstrumentation())
    .WithTracing(t => t.AddNimBusInstrumentation());
```

- Depends on `OpenTelemetry.Api`, `OpenTelemetry.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`. No SDK or exporter dependency.
- All three extension methods are idempotent.
- The provider-builder extensions still work standalone (no `Services.AddNimBusInstrumentation` call) but produce a documented degraded mode: meters/sources registered, no decorators, no gauge values (FR-006).
- Public `NimBusInstrumentation` static class exposes canonical name constants for hand-rolled OTel pipelines.

## Activity sources and spans (FR-010..FR-015)

| Source | Spans |
|---|---|
| `NimBus.Publisher` | `NimBus.Publish` (Producer) |
| `NimBus.Consumer` | `NimBus.Process` (Consumer) |
| `NimBus.Outbox` | `NimBus.Outbox.Enqueue`, `NimBus.Outbox.Dispatch` (Producer), `NimBus.Outbox.Cleanup` |
| `NimBus.DeferredProcessor` | `NimBus.DeferredProcessor.Park`, `NimBus.DeferredProcessor.Replay` |
| `NimBus.Resolver` | `NimBus.Resolver.RecordOutcome`, `NimBus.Resolver.RecordAudit` |
| `NimBus.Store` | `NimBus.Store.{Operation}` (verbose-only) |

- The legacy `"NimBus"` source (in `NimBusDiagnostics.cs`) is **deleted**, not aliased. The whole `NimBusDiagnostics` static class goes — `Source`, `ActivitySourceName`, `DiagnosticIdProperty` all removed.
- The `NimBus.Process` span lifetime extends to cover broker settle (`Complete` / `Abandon` / `DeadLetter` / `Defer`), not just handler return.
- Publish-side instrumentation lives in an `ISender` decorator in `NimBus.Core` (transport-agnostic), parallel to the existing `OutboxSender`.
- Outbox dispatch: the dispatcher span attaches the original (write-time) `Activity` as an `ActivityLink`, *not* as a parent — causal but not temporal.

## Span attributes (FR-020..FR-025)

Canonical reference: OpenTelemetry messaging semantic conventions ([general spans](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/), [Azure messaging](https://opentelemetry.io/docs/specs/semconv/messaging/azure-messaging/)) version **1.41** or latest stable at release.

OTel `messaging.*` (canonical):

- `messaging.system` — `servicebus` (Azure Service Bus), `nimbus.inmemory`. **Note:** `servicebus`, not `azureservicebus`.
- `messaging.operation.type` — `publish | receive | process | settle`. **Note:** `messaging.operation.type`, not `messaging.operation`.
- `messaging.operation.name` (optional, system-specific) — e.g., `complete`, `abandon`, `defer` for settle ops on Service Bus.
- `messaging.destination.name`
- `messaging.message.id`, `messaging.message.conversation_id`, `messaging.message.body.size`

`messaging.destination.kind` is **not** emitted (deprecated by OTel; backends infer kind from `messaging.system` + destination shape).

Span name pattern (FR-015): messaging spans use `{operation.type} {destination.name}` (e.g., `publish my-queue`, `process my-queue`). Internal-only spans (Resolver, DeferredProcessor, Outbox.Cleanup, Store) use stable identifiers without destination embedding.

Transport-specific namespaces (`messaging.servicebus.*`) MAY be set in addition to the generic ones (FR-023). Examples:

- `messaging.servicebus.message.delivery_count`, `messaging.servicebus.message.enqueued_time`, `messaging.servicebus.destination.subscription_name`

Session key exposure (FR-024): both `nimbus.session.key` and `messaging.servicebus.message.session.id` (Service Bus only).

NimBus-specific (`nimbus.*`):

- `nimbus.event_type`, `nimbus.session.key` (spans only — never on metrics)
- `nimbus.outcome` (`completed | dead_lettered | deferred | abandoned | permanent_failure | transient_failure`)
- `nimbus.permanent_failure`, `nimbus.delivery_count`, `nimbus.handler.type`, `nimbus.endpoint`, `nimbus.has_parent_trace`

Errors per OTel exception convention: `ActivityStatusCode.Error`, `exception` event, `error.type`.

Future incompatible revisions of OTel messaging semconv are handled as breaking changes (FR-025); no silent dual-emission.

## Trace propagation (FR-030..FR-033)

- W3C `traceparent` is the canonical format. The legacy `Diagnostic-Id` Service Bus application property is **removed from both write and read paths** — no transitional dual-read. In-flight messages produced by older NimBus builds at upgrade time lose parent-trace linkage; documented as the upgrade impact.
- Both transports use the same property names (`traceparent`, `tracestate`).
- Outbox rows persist `traceparent` / `tracestate` (FR-032). Concretely:
  - **Model**: `OutboxMessage` (`src/NimBus.Core/Outbox/OutboxMessage.cs`) gains `string? TraceParent`, `string? TraceState`. Today it stores only `CorrelationId`, which is insufficient for W3C trace propagation.
  - **SQL Server schema**: outbox table gains `TraceParent NVARCHAR(55) NULL` and `TraceState NVARCHAR(256) NULL` columns. Idempotent schema-init in `SqlServerOutbox` adds the columns; existing deployments migrate via `ALTER TABLE ... ADD ... IF NOT EXISTS` semantics.
  - **Population**: `OutboxSender` captures `Activity.Current?.Id` and `Activity.Current?.TraceStateString` at write time.
  - **Restoration**: `OutboxDispatcher` sets the captured `traceparent` / `tracestate` on the outgoing message and creates the dispatch span as a root span linked to the original.
  - **Pre-existing rows without trace context** (`TraceParent IS NULL`): dispatch successfully — no exception, no startup failure. Resulting span has no link; an `ActivityEvent` named `nimbus.outbox.orphan_row` is emitted at INFO level. Conformance suite covers this case.
- `Baggage` is a transparent pass-through.

## Metrics (FR-040..FR-045)

Meters: `NimBus.Publisher`, `NimBus.Consumer`, `NimBus.Outbox`, `NimBus.DeferredProcessor`, `NimBus.Resolver`, `NimBus.Store`. Legacy `NimBus.Pipeline` and `NimBus.ServiceBus` meters are **deleted outright**, not aliased.

Highlights of the new instrument set:

- **Publisher**: `nimbus.message.published` (counter), `nimbus.message.publish.duration` (histogram), `nimbus.message.publish.failed` (counter)
- **Consumer**: `nimbus.message.received`, `nimbus.message.processed`, `nimbus.message.process.duration`, `nimbus.message.queue_wait`, `nimbus.message.e2e_latency`
- **Outbox**: `nimbus.outbox.enqueued`, `nimbus.outbox.dispatched`, `nimbus.outbox.dispatch.duration`, `nimbus.outbox.pending` (gauge), `nimbus.outbox.dispatch_lag` (gauge)
- **DeferredProcessor**: `nimbus.deferred.parked`, `nimbus.deferred.replayed`, `nimbus.deferred.replay.duration`, `nimbus.deferred.pending` (gauge), `nimbus.deferred.blocked_sessions` (gauge)
- **Resolver**: `nimbus.resolver.outcome_written`, `nimbus.resolver.audit_written`, `nimbus.resolver.write.duration`
- **Store**: `nimbus.store.operation.duration`, `nimbus.store.operation.failed`

Cardinality rule (FR-045): `messaging.message.id`, `nimbus.session.key`, and `messaging.message.conversation_id` are **span-only**, never metric attributes.

## Component instrumentation (FR-050..FR-056)

- **Publisher**: `InstrumentingSenderDecorator` registered automatically by `AddNimBusPublisher` / `AddNimBusSubscriber`.
- **Consumer**: `MetricsMiddleware` updated to emit under `NimBus.Consumer` meter; consumer span lifetime extended to broker settle.
- **Outbox**: `OutboxDispatcher` (`src/NimBus.Core/Outbox/OutboxDispatcher.cs`) emits `nimbus.outbox.*` and `NimBus.Outbox.Dispatch` spans. **New query contract** `IOutboxMetricsQuery` with `GetPendingCountAsync()` and `GetOldestPendingEnqueuedAtUtcAsync()` (FR-052) — required because today's `IOutbox` (`src/NimBus.Core/Outbox/IOutbox.cs`) only has `GetPendingAsync(batchSize)` plus mark/store, which cannot produce exact pending count or oldest-row lag without abusing batched reads. `SqlServerOutbox` implements via single-statement queries. When no provider implements the query interface, the gauges emit no values and a one-time INFO log is emitted.
- **Deferred**: `DeferredMessageProcessor` (currently `src/NimBus.ServiceBus/DeferredMessageProcessor.cs`; eventually relocated to `NimBus.Core` per spec 003 FR-050) emits `nimbus.deferred.*`.
- **Resolver**: `ResolverService` (`src/NimBus.Resolver/Services/ResolverService.cs`) emits `nimbus.resolver.*` and `NimBus.Resolver.*` spans.
- **Store**: `InstrumentingMessageTrackingStoreDecorator` registered automatically by every storage-provider extension. Both Cosmos DB and SQL Server providers wrapped identically; differentiated by `nimbus.store.provider` attribute.
- **Hosts**: `NimBus.WebApp` and `NimBus.Resolver` add the package and call `AddNimBusInstrumentation()`.

## ServiceDefaults integration (FR-060..FR-062)

`NimBus.ServiceDefaults.Extensions.ConfigureOpenTelemetry` is rewritten to call `AddNimBusInstrumentation()` for both providers, replacing hard-coded `AddMeter("NimBus.ServiceBus")` and `AddSource("NimBus")`. After this feature, `NimBus.ServiceDefaults` contains zero hard-coded NimBus meter / source string literals.

## Configuration (FR-070..FR-071)

`NimBusOpenTelemetryOptions`:

- `Verbose` (bool, default `false`) — enable per-step spans.
- `IncludeMessageHeaders` (bool, default `false`) — selected NimBus headers as span events; never message body.
- `GaugePollInterval` (TimeSpan, default 30s).
- `OutboxLagWarnThreshold` (TimeSpan?, optional).

Bound from `NimBus:Otel` configuration section (`NimBus__Otel__Verbose=true`).

## Testing (FR-080..FR-085)

- New project `tests/NimBus.OpenTelemetry.Tests/` using `InMemoryExporter` for metrics and traces.
- Asserts: per-handler emission, propagation round-trip, error attribution, cardinality (deny-list of per-message attributes).
- *Instrumentation* category added to `NimBus.Testing.Conformance` and run against every registered transport (in-memory + Service Bus today).

## Documentation (FR-090..FR-092)

- New `docs/observability.md` — package, registration, attribute mapping, propagation guarantees, verbose mode.
- Sample dashboard JSON for Aspire and Grafana: *Publisher health*, *Consumer health*, *Outbox lag*, *Resolver throughput*.
- Upgrade-impact section — meter / source / property / attribute renames, dashboard search-and-replace recipe, in-flight-message warning.

## Breaking changes (FR-100..FR-104)

This feature is a **breaking change** to the instrumentation surface. There are no aliases, no `[Obsolete]` shims, no transitional dual-emission.

- `NimBusDiagnostics` (with `Source`, `ActivitySourceName`, `DiagnosticIdProperty`) is **deleted**.
- The `"NimBus"` activity source name disappears; replaced by `NimBus.Consumer`.
- The `"NimBus.ServiceBus"` and `"NimBus.Pipeline"` meter names disappear; replaced by `NimBus.Consumer` and the rest of the new meter set.
- The `Diagnostic-Id` Service Bus application property is removed from both write and read paths. NimBus emits and consumes `traceparent` / `tracestate` only.
- The legacy attribute names (`messaging.destination`, `messaging.event_type`, `messaging.message_id`) are renamed to the OTel canonical forms (`messaging.destination.name`, `nimbus.event_type`, `messaging.message.id`).
- `MetricsMiddleware` keeps its public type and constructor; only the internal meter / instrument names change.

Upgrade impact (must be in `docs/observability.md` and release notes):

- Operators rename meter / source / attribute references in their dashboards (PromQL / KQL search-and-replace recipe provided).
- In-flight messages produced by an older NimBus build at the time of the upgrade lose parent-trace linkage (their `Diagnostic-Id` is no longer read).

## Acceptance criteria

- [ ] **SC-001** — Three-call wiring (`Services.AddNimBusInstrumentation`, `WithMetrics(...AddNimBusInstrumentation())`, `WithTracing(...AddNimBusInstrumentation())`) enables the full instrumentation surface. No NimBus meter / source / attribute names in the host's source code.
- [ ] **SC-002** — Publish→consume produces a single distributed trace; publisher Producer span is the parent of the consumer Consumer span; renders correctly in Aspire dashboard and any OTel-conformant backend without manual mapping.
- [ ] **SC-003** — Every component (publisher, consumer, outbox, deferred, resolver, store) emits at least one span and one metric per operation. Verified by the conformance suite.
- [ ] **SC-004** — All NimBus span / metric attribute names match OpenTelemetry `messaging.*` v1.27+ conventions; NimBus-specific attributes use `nimbus.*`.
- [ ] **SC-005** — `NimBus.ServiceDefaults` contains zero hard-coded NimBus meter / source string literals (`grep` returns no matches).
- [ ] **SC-006** — Repository-wide `grep` returns no matches for the legacy meter / source / property names (`NimBus.ServiceBus`, `NimBus.Pipeline`, the `"NimBus"` source string, `Diagnostic-Id`) outside the upgrade-impact documentation.
- [ ] **SC-007** — Per-message instrumentation overhead ≤ 5% of median handler runtime at p99 when `Verbose = false` (BenchmarkDotNet).
- [ ] **SC-008** — No metric attribute set contains a per-message identifier (verified by deny-list assertion).
- [ ] **SC-009** — Transport conformance *Instrumentation* category passes for in-memory + Service Bus; any future transport adapter joins this set with no source changes to `NimBus.OpenTelemetry`.
- [ ] **SC-010** — Outbox-style propagation: HTTP-request span → outbox row → dispatcher span shows up as a single trace id, with the original span as an `ActivityLink` on the dispatcher.

## Sub-issue breakdown

Phase 4.1 — Package, public API, consumer/publisher migration. **Decision gate at end.**

- [ ] Sub-issue: **Create `NimBus.OpenTelemetry` project and NuGet metadata** — csproj, `Directory.Packages.props` entries, README.
- [ ] Sub-issue: **`NimBusInstrumentation` constants + `NimBusActivitySources` / `NimBusMeters` internal holders** — single source of truth for names.
- [ ] Sub-issue: **`IServiceCollection.AddNimBusInstrumentation` extension** — registers `IOptions<NimBusOpenTelemetryOptions>`, `InstrumentingSenderDecorator`, `InstrumentingMessageTrackingStoreDecorator`, `NimBusGaugeBackgroundService`. Idempotent.
- [ ] Sub-issue: **`MeterProviderBuilder` / `TracerProviderBuilder` `AddNimBusInstrumentation` extensions** — `AddMeter` / `AddSource` calls only; idempotent; usable standalone in degraded mode.
- [ ] Sub-issue: **`InstrumentingSenderDecorator` in `NimBus.Core`** — emits `NimBus.Publish` + publisher metrics; auto-registered by `Services.AddNimBusInstrumentation`.
- [ ] Sub-issue: **Migrate consumer span to `NimBus.Consumer` source** — extend lifetime to broker settle; **delete `NimBusDiagnostics` outright** (no alias).
- [ ] Sub-issue: **Update `MetricsMiddleware` to emit under `NimBus.Consumer` meter** — `"NimBus.Pipeline"` removed, not aliased.
- [ ] Sub-issue: **Replace `Diagnostic-Id` with `traceparent` everywhere** — `MessageHelper` updates; remove `Diagnostic-Id` from both write and read paths.
- [ ] Sub-issue: **Align span attributes and span names with OTel messaging semconv 1.41+** — `messaging.system = servicebus`, `messaging.operation.type` (not `messaging.operation`), no `messaging.destination.kind`, `messaging.servicebus.*` (not `messaging.azureservicebus.*`), span name pattern `{operation.type} {destination.name}` for messaging spans. Stable identifiers retained for internal-only spans.
- [ ] Sub-issue: **Rewire `NimBus.ServiceDefaults.ConfigureOpenTelemetry` to call `AddNimBusInstrumentation()`** — remove hard-coded names.
- [ ] Sub-issue: **`tests/NimBus.OpenTelemetry.Tests/` with in-memory exporters** — propagation round-trip, error attribution, cardinality deny-list, idempotent registration.
- [ ] Sub-issue: **Upgrade-impact documentation** — `docs/observability.md` upgrade section + release notes; PromQL / KQL search-and-replace recipe.
- [ ] **Decision gate**: review public API surface; promote to stable only after one minor-version dogfooding.

Phase 4.2 — Outbox, deferred-processor, resolver, store instrumentation.

- [ ] Sub-issue: **Outbox model + schema migration for trace context** — `OutboxMessage.TraceParent`, `TraceState`; SQL Server columns `TraceParent NVARCHAR(55) NULL`, `TraceState NVARCHAR(256) NULL`; idempotent schema-init; orphan-row handling for pre-existing `NULL` rows.
- [ ] Sub-issue: **Outbox instrumentation** — `OutboxSender` captures trace context + emits Enqueue span; `OutboxDispatcher` restores context, emits Dispatch span as root with `ActivityLink` to original; `nimbus.outbox.*` instruments.
- [ ] Sub-issue: **`IOutboxMetricsQuery` query contract** — `GetPendingCountAsync()` + `GetOldestPendingEnqueuedAtUtcAsync()`; SQL Server implementation via single-statement queries; gauges emit no values and a one-time INFO log when no provider implements it.
- [ ] Sub-issue: **DeferredProcessor instrumentation** — Park / Replay spans, `nimbus.deferred.*` instruments, blocked-sessions gauge.
- [ ] Sub-issue: **ResolverService instrumentation** — `RecordOutcome` / `RecordAudit` spans, `nimbus.resolver.*` instruments.
- [ ] Sub-issue: **`InstrumentingMessageTrackingStoreDecorator`** — wraps both Cosmos and SQL providers; `nimbus.store.*` instruments; verbose-only spans.
- [ ] Sub-issue: **`NimBusGaugeBackgroundService`** — polls outbox + deferred + store at the configured interval to drive observable gauges.
- [ ] Sub-issue: **Conformance suite *Instrumentation* category** — runs against every registered transport.

Phase 4.3 — Verbose mode, headers, dashboards, docs.

- [ ] Sub-issue: **Verbose mode wrapper** — per-pipeline-behavior child spans, per-store-operation child spans; no PII / body content even when on.
- [ ] Sub-issue: **`IncludeMessageHeaders` option** — selected NimBus headers as span events; allow-list approach.
- [ ] Sub-issue: **`docs/observability.md`** — package guide, attribute reference, propagation guarantees, sampler / exporter recommendations.
- [ ] Sub-issue: **Sample dashboards** — Aspire dashboard JSON + Grafana JSON for the four hot dashboards.
- [ ] Sub-issue: **Upgrade impact** — current → new wiring, dashboard rename recipe (PromQL / KQL), in-flight-message warning.

## Edge cases

- High-cardinality attributes — span-only for per-message ids; metrics attributes are bounded.
- Message body — never serialized to attributes; only `messaging.message.body.size` (length).
- PII in event-type ids — documented warning, no automatic redaction.
- Sampling — instrumentation does not configure samplers; `ParentBased(TraceIdRatioBased)` is the recommendation.
- Span end ordering — consumer span ends *after* settle, not after handler return.
- Outbox-published failures — separate spans for failed publish and successful dispatch; not merged.
- In-memory transport — emits identical instrumentation; tests rely on this.
- Nested handler invocations — downstream publish span is a child of the in-flight `NimBus.Process`.
- Activity disabled at source — `ActivitySource.HasListeners()` makes this silent.
- Resolver inline vs Function — same instruments; differentiated only by `service.name` resource attribute.
- OTel SDK 1.x → 2.x — major-version bump of `NimBus.OpenTelemetry`, not silent absorption.
- Non-Aspire hosts — package targets `IHostBuilder` / `IHostApplicationBuilder`; no Aspire-only deps.
- Azure Functions isolated worker — coexists with Functions OTel pipeline; additive.
- `ActivityLink` size limits — drop oldest links beyond 32, no throw.
- Registration order — `AddNimBusInstrumentation` is order-independent vs `AddNimBusPublisher` / `AddNimBusSubscriber`.

## Out of scope

- Capturing message body content as span attributes / events.
- Shipping a NimBus-branded exporter (OTLP / AppInsights / Prometheus).
- Custom NimBus sampler.
- Continuous profiling / eBPF.
- Custom log enrichers beyond what the OTel logging provider already does.
- Replacing `Microsoft.Extensions.Logging` (ADR-006 stays).
- Web UI for browsing traces / metrics.
- Per-handler dashboards or auto-generated alerts.
- Cross-process Baggage propagation policies.
- A `NimBus.OpenTelemetry.Aspire` package (handled by `NimBus.ServiceDefaults`).

## Open questions

- **`NimBus.Pipeline` meter — fold into `NimBus.Consumer` or keep separate?** Proposed: fold in. The legacy `NimBus.Pipeline` meter is deleted outright per FR-100.
- ~~**`messaging.destination.kind`**~~ **Resolved** (FR-020): not emitted.
- **`messaging.message.body.size` semantics on Cosmos-stored messages** — raw broker bytes or post-deserialize bytes? Proposed: raw broker bytes.
- **`nimbus.handler.type` cardinality risk on histograms** — include with documented bound (per-deployment handler list)?
- **Outbox-pending gauge cost** — exact `count(*)` vs approximate proxy? Proposed: exact + configurable interval.
- **Package name** — `NimBus.OpenTelemetry` vs `NimBus.Diagnostics`? Proposed: `NimBus.OpenTelemetry`.
- **Default for `Verbose`** — Proposed: off by default everywhere.
- **Trace context on dead-letter resubmit** — original chain or fresh trace with link? Proposed: fresh + link.
- **Resolver inline-mode span parenting** — child of `NimBus.Process` when inline; root when standalone Function.

## Suggested labels

`enhancement` · `observability` · `opentelemetry` · `metrics` · `tracing` · `documentation` · `epic`
