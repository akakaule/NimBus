# NimBus Roadmap

## Competitive Position

NimBus is a mature, Azure-native event-driven integration platform with strong differentiators: session-based ordered processing, centralized Resolver with full audit trail, management WebApp with resubmit/skip, and declarative topology provisioning. However, compared to mature open-source frameworks (MassTransit, Wolverine, NServiceBus, CAP, Rebus, Brighter), NimBus has gaps that affect reliability, developer experience, and observability. This roadmap closes those gaps while preserving NimBus's unique strengths.

| | NimBus Strength | Competitor Advantage |
|---|---|---|
| **Ordering** | Session-based FIFO with deferred replay | Wolverine has partitioned sequential; most others lack this |
| **Audit trail** | Centralized Resolver + Cosmos projections | Only NServiceBus (ServiceControl) has comparable |
| **Ops UI** | WebApp with resubmit/skip | Only NServiceBus (ServicePulse) and CAP (dashboard) |
| **Reliability** | Transactional outbox (Phase 1) | All mature frameworks have transactional outbox |
| **Observability** | OpenTelemetry tracing + metrics dashboard (Phase 2) | All competitors have OpenTelemetry Activity tracing |
| **DX** | DI integration + Aspire sample + NuGet packages | All competitors integrate with MS DI natively |
| **Extensibility** | Middleware pipeline + extension framework with 3 built-in behaviors | NServiceBus (behaviors), MassTransit (filters), Brighter (decorators) |
| **Testing** | In-memory transport + 14+ E2E tests (Phase 2) | MassTransit, Wolverine, Rebus have in-memory transports |
| **Workflows** | Orchestration via services + scheduling SDK (ADR-009) | NServiceBus, MassTransit, Wolverine have built-in saga/state machines |
| **Resilience** | Retry policies (3 strategies) | Wolverine/Brighter have circuit breakers; NServiceBus has two-phase retries |
| **Large messages** | None (Service Bus 256KB limit) | NServiceBus has DataBus/claim-check pattern |
| **Messaging patterns** | Publish/subscribe only | MassTransit has request/response; all have message scheduling |
| **Transport** | Azure Service Bus + RabbitMQ (Phase 6, on-premise) | Most support Kafka, SQL, in-memory in addition |
| **Local/on-prem deployment** | RabbitMQ + SQL Server, no Azure dependency (Phase 6) | NServiceBus / Wolverine / MassTransit support multiple on-prem transports natively |

## Roadmap Phases

### Phase 1: Foundation & Reliability (Q2 2026) -- Implemented

Goal: Close the #1 reliability gap and improve developer experience.

**1.1 Transactional Outbox Pattern**

The most critical missing feature. Without an outbox, if a process crashes between committing a database change and sending the message, the message is lost silently.

- `IOutbox` abstraction in `NimBus.Core/Outbox/`
- SQL Server implementation in `NimBus.Outbox.SqlServer/`
- `OutboxSender` transparently intercepts `ISender.Send()` to write to outbox instead of Service Bus
- `OutboxDispatcher` polls outbox, sends to real `ISender`, marks dispatched
- `IOutboxCleanup` for purging delivered messages

Key paths:
- `src/NimBus.Core/Outbox/IOutbox.cs`
- `src/NimBus.Core/Outbox/OutboxSender.cs`
- `src/NimBus.Core/Outbox/OutboxDispatcher.cs`
- `src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs`

**1.2 Microsoft.Extensions.DependencyInjection Integration**

Replace factory-based handler registration with standard DI.

```csharp
// Before (factory-based, now [Obsolete])
subscriber.RegisterHandler<OrderPlaced>(() => new OrderPlacedHandler(dep1, dep2));

// After (DI-based)
services.AddNimBusSubscriber("orders", builder => {
    builder.AddHandler<OrderPlaced, OrderPlacedHandler>();
});
```

- `AddNimBusPublisher()`, `AddNimBusSubscriber()`, `AddNimBusOutboxDispatcher()` extension methods
- Handler resolution via `IServiceProvider.GetRequiredService<IEventHandler<T>>()`
- Existing factory API preserved as `[Obsolete]`

Key paths:
- `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`
- `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`
- `src/NimBus.SDK/Hosting/OutboxDispatcherHostedService.cs`

**1.3 Configurable Retry Policies**

Previously hardcoded in `RetryDefinitions`. Now configuration-driven.

- `RetryPolicy` record with `BackoffStrategy` (Fixed, Linear, Exponential), `BaseDelay`, `MaxDelay`
- `IRetryPolicyProvider` + `DefaultRetryPolicyProvider` with per-event-type, exception-based, and default policies
- Old `RetryDefinitions` marked `[Obsolete]`, implements `IRetryPolicyProvider` as bridge

Key paths:
- `src/NimBus.Core/Messages/RetryPolicy.cs`
- `src/NimBus.Core/Messages/IRetryPolicyProvider.cs`
- `src/NimBus.Core/Messages/DefaultRetryPolicyProvider.cs`

---

### Phase 2: Observability & Testing (Q3 2026) -- Implemented

Goal: Production-grade observability and fast inner-loop testing.

**2.1 OpenTelemetry Distributed Tracing**

NimBus has metrics (`Meter`/`Histogram` in `ServiceBusAdapter`) but lacks `Activity`-based distributed tracing. Traces should flow across publish, Service Bus, subscribe, and Resolver boundaries.

- Create `ActivitySource` in `NimBus.ServiceBus`
- Start activities on publish (`PublisherClient.Publish`) and subscribe (`ServiceBusAdapter.Handle`)
- Propagate W3C TraceContext via Service Bus `Diagnostic-Id` property
- Link parent/child activities across message boundaries
- Enrich activities with NimBus-specific tags (EventTypeId, EndpointId, SessionId, MessageType)

Reference: MassTransit's `DiagnosticHeaders` and Brighter's W3C TraceContext implementation.

**2.2 In-Memory Transport for Testing**

Allow running the full NimBus message pipeline without Azure Service Bus.

- Implement `InMemoryServiceBusClient` that mimics topic/subscription routing
- Session support via in-memory session state
- Wire into `SubscriberClient` and `PublisherClient` via the existing `ServiceBusClient` parameter
- Publish as a `NimBus.Testing` project

Benefits: unit tests without Azure credentials, fast/deterministic integration tests, no Service Bus dependency in CI/CD.

**2.3 Health Checks**

Standard `IHealthCheck` implementations for ASP.NET Core.

- Service Bus connectivity check
- Cosmos DB connectivity check
- Resolver lag check (time since last processed message)
- Register via `services.AddNimBusHealthChecks()`

---

### Phase 3: Extensibility & Patterns (Q4 2026) -- Partially Implemented

Goal: Enable cross-cutting concerns and more complex workflows.

**3.1 Middleware Pipeline** -- Implemented

`IMessagePipelineBehavior` with delegate-based pipeline composition, wired into `StrictMessageHandler` via `MessagePipeline`. Registered via `AddNimBus(builder => builder.AddPipelineBehavior<T>())`.

- ~~`IMessagePipelineBehavior` with `Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct)`~~
- ~~Chain behaviors in `MessageHandler` before message type dispatch~~
- ~~Built-in middleware: `LoggingMiddleware`, `MetricsMiddleware`, `ValidationMiddleware`~~
- ~~`MessageAlreadyDeadLetteredException` for correct lifecycle event handling when middleware dead-letters~~
- ~~15 unit tests covering all built-in middleware~~
- ~~Documentation: `docs/pipeline-middleware.md`~~

Additional completed work:
- ~~Removed `NimBus.Core.Logging` abstraction — handlers use standard `Microsoft.Extensions.Logging`~~
- ~~Clean handler signature: `Handle(T message, IEventHandlerContext context, CancellationToken ct)` (no logger parameter)~~
- ~~Separated DeferredProcessor — deferred processing handled by dedicated service in subscriber, not StrictMessageHandler~~
- ~~Metrics renamed from `dis.*` to `nimbus.*`~~

**3.2 Inbox Pattern (Idempotent Consumers)**

Paired with the outbox for exactly-once semantics on the consumer side.

- Track processed `MessageId` values in a deduplication store (Cosmos DB or SQL)
- Check before handler invocation; skip if already processed
- Configurable TTL for deduplication entries
- Optional -- consumers opt in via configuration

**3.3 Message Scheduling SDK**

Expose Azure Service Bus's native scheduling as a first-class SDK feature. Enables workflow timeouts in orchestration services.

- `ISender.ScheduleMessage(IMessage, DateTimeOffset)` returning a cancellable `long sequenceNumber`
- `ISender.CancelScheduledMessage(long sequenceNumber)`
- Delegates to `ServiceBusSender.ScheduleMessageAsync` / `CancelScheduledMessageAsync`
- Outbox integration: scheduled messages written to outbox with `ScheduledEnqueueTimeUtc`

Reference: MassTransit's `IMessageScheduler`, Rebus's `bus.Defer()`.

**3.4 Poison Message Classification**

Distinguish transient failures (timeouts, 503s) from permanent failures (validation, deserialization). Permanent failures dead-letter immediately without wasting retry budget.

- `IPermanentFailureClassifier` interface with default implementation for common exception types
- Configurable per event type via `DefaultRetryPolicyProvider`
- Extends existing retry pipeline in `StrictMessageHandler`

Reference: NServiceBus two-phase recoverability (immediate + delayed retries).

**3.5 Circuit Breaker Middleware**

Pause message processing when a downstream dependency is systematically failing. Without it, every message burns retries against a broken dependency.

- `CircuitBreakerMiddleware` implementing `IMessagePipelineBehavior`
- Uses Polly V8 resilience pipeline
- Configurable failure threshold, break duration, and half-open test count
- When open: messages are abandoned (returned to queue for later), not dead-lettered
- Per-endpoint or per-event-type configuration

Reference: Wolverine per-endpoint circuit breaker, Brighter `[UseResiliencePipeline]`.

**3.6 Orchestration Pattern Guide**

Multi-step workflows (sagas) are implemented as application-level orchestration services, not as a NimBus framework feature. NimBus provides the messaging primitives; workflow coordination is plain C# in a dedicated service. See [ADR-009](adr/009-orchestration-via-application-services.md).

- Orchestration pattern documentation (`docs/orchestration.md`)
- Sample orchestrator service extending the Aspire Pub/Sub sample
- Timeout patterns using `ScheduleMessage` / `CancelScheduledMessage` (Phase 3.3)
- Compensation patterns via published events

---

### Phase 4: Platform Maturity (H1 2027) -- In Progress

Goal: Production hardening, developer experience polish, and ecosystem growth.

**4.1 WebApp Enhancements** -- Mostly Complete

- ~~Dashboard metrics: time-series area chart with gap-filling, KPI summary cards, event-type-level breakdown~~
- ~~Failed message insights: error pattern grouping and Insights page~~
- ~~Audit log search: filterable audit search API and UI~~
- ~~Bulk operations: subscription purge, delete by status, skip messages, delete by destination, copy endpoint data, delete all events -- all with preview/confirm UI and EIP_Management authorization~~
- ~~Comment/audit section on event details with /api/me user info~~
- ~~Reprocess deferred button and API for orphaned deferred messages~~
- ~~Delete button on event details for failed/deadlettered/unsupported events~~
- ~~Grouped by Error view on endpoint details with bulk Resubmit All / Skip All~~
- ~~Admin accordion redesign: Recovery, Cleanup, Infrastructure, Danger Zone sections~~
- ~~EnumMemberModelBinder for correct period/enum query string binding~~
- ~~Millisecond precision in all datetime displays~~
- ~~Message flow visualization: timeline view in Event Details showing full message lifecycle~~
- Alerting: webhook/email notifications for failed messages, dead-letters, or session blocks

**4.2 Claim-Check Pattern**

Offload large message payloads to Azure Blob Storage, passing a reference through Service Bus. Solves the 256KB/1MB message size limit.

- `ClaimCheckMiddleware` implementing `IMessagePipelineBehavior`
- Configurable size threshold (e.g., >200KB triggers offload)
- Publisher side: serialize payload to Blob Storage, replace body with blob reference
- Consumer side: detect claim-check reference, retrieve payload from Blob Storage
- Package as `NimBus.Extensions.ClaimCheck`

Reference: NServiceBus DataBus / `[DataBusProperty]`, Azure claim-check pattern.

**4.3 Request/Response**

Synchronous request/response over the bus. A publisher sends a typed request and awaits a typed response with timeout handling.

- `ISender.Request<TRequest, TResponse>(TRequest, TimeSpan timeout)` returning `Task<TResponse>`
- Uses Azure Service Bus reply-to queue (session-based for correlation)
- Timeout handling with `OperationCanceledException`
- Enables query patterns between services without separate HTTP APIs

Reference: MassTransit `IRequestClient<T>`.

**4.4 Failed Message Hook**

Application-level last-chance handler before dead-lettering. After retries are exhausted, dispatch to `IFailed<T>` for custom recovery, enriched diagnostics, or re-routing.

- `IFailedMessageHandler<T>` interface invoked after retry exhaustion, before dead-letter
- Handler can: log enriched diagnostics, send to alternative endpoint, modify and retry, or allow dead-letter
- Registered via `builder.AddFailedHandler<OrderPlaced, OrderPlacedFailedHandler>()`

Reference: Rebus `IFailed<T>`, MassTransit `Fault<T>`.

**4.5 SDK Developer Experience**

- Source generators: replace reflection-based event type discovery with compile-time source generators
- Strongly-typed configuration: `NimBusOptions` pattern with validation
- Better error messages: actionable errors when misconfigured
- Message versioning: additive nullable fields, inheritance-based polymorphic dispatch, `[MessageVersion]` attribute

**4.6 Rate Limiting Middleware**

Control message consumption rate to protect downstream services and manage API quotas.

- `RateLimitingMiddleware` implementing `IMessagePipelineBehavior`
- Uses `System.Threading.RateLimiting` (token bucket, sliding window, fixed window)
- Configurable per event type or per endpoint
- When limit exceeded: messages are abandoned (returned to queue), not dead-lettered

**4.7 Notification Channels**

Production-ready notification channels extending `NimBus.Extensions.Notifications`.

- Webhook channel (HTTP POST with configurable payload template)
- Microsoft Teams channel (incoming webhook connector)
- Email channel (SendGrid or SMTP)
- Severity-based routing: route Critical to all channels, Warning to webhook only
- Rate limiting and batching to prevent notification storms
- Completes the alerting feature (4.1)

**4.8 Transport Abstraction** -- Committed (delivered in Phase 6)

The original "evaluate only" framing assumed no concrete demand. Concrete demand has materialized: on-premise / cloud-agnostic teams that cannot adopt Azure Service Bus. The work is now committed and delivered as Phase 6 (*Multi-transport: RabbitMQ on-premise*). See [ADR-011](adr/011-rabbitmq-as-second-transport.md) for the design.

**4.9 Documentation & Onboarding** -- Mostly Complete

- ~~Sample applications: Aspire Pub/Sub sample with Publisher, Subscriber (with DeferredProcessorService), and middleware demo~~
- ~~CI/CD documentation: GitHub Actions and Azure DevOps deploy pipelines~~
- ~~README: local development (Aspire) and CI/CD setup instructions~~
- ~~Message flow documentation: `docs/message-flows.md` with 10 flow diagrams~~
- ~~Deferred message processing guide: `docs/deferred-messages.md` with Mermaid sequence diagrams~~
- ~~Pipeline middleware documentation: `docs/pipeline-middleware.md` with patterns and API reference~~
- ~~Getting started guide: `docs/getting-started.md`~~
- ~~SDK API reference: `docs/sdk-api-reference.md`~~
- ~~Architecture decision records: 8 ADRs in `docs/adr/`~~
- ~~CLI reference: `docs/cli.md`~~
- ~~Azure Functions hosting guide: `docs/azure-functions-hosting.md`~~
- Migration guide from MassTransit/NServiceBus

---

### Phase 5: Ecosystem & Scale (H2 2027+) -- In Progress

Goal: If NimBus is to be open-sourced or adopted beyond its current org.

**5.1 NuGet Package Publishing** -- Implemented

- ~~NuGet package metadata in `Directory.Build.props`~~
- ~~SourceLink configuration for source debugging~~
- ~~GitHub Actions publish workflow (`nuget-publish.yml`)~~
- ~~MIT license~~
- ~~Five packable projects: Abstractions, Core, ServiceBus, SDK, CommandLine~~
- Public API surface review and stability guarantees

**5.2 Multi-Tenant Support**

- Tenant-isolated topics/subscriptions
- Per-tenant configuration and routing

**5.3 Event Sourcing Integration (Optional)**

- If there's demand, integrate with Marten or a custom event store
- Keep as a separate `NimBus.EventSourcing` package

---

### Phase 6: Multi-transport -- RabbitMQ on-premise

Goal: enable on-premise / cloud-agnostic deployments with no Azure dependency. Delivered in four gated phases per [ADR-011](adr/011-rabbitmq-as-second-transport.md).

**6.1 `NimBus.Transport.Abstractions` extraction (refactor, no behavioural change)**

- New project housing `ISender`, `IMessageContext`, `IReceivedMessage`, `IMessageHandler`, `IDeferredMessageProcessor`, plus new `ITransportProviderRegistration`, `ITransportCapabilities`, `ITransportManagement` markers
- Fix existing leakage: drop `Azure.Messaging.ServiceBus` references from `NimBus.SDK`, `NimBus.Resolver`, `NimBus.WebApp`
- Move deferred-by-session out of the transport layer into `NimBus.Core` (park in `MessageStore` keyed by session, replay on unblock — works for any transport)
- New transport conformance suite in `NimBus.Testing.Conformance`, mirroring the storage conformance suite
- `NimBus__Transport` env-var bridged through `NimBus.AppHost` and `samples/CrmErpDemo/CrmErpDemo.AppHost`, default `servicebus`
- **Decision gate at end of this phase**: review the seam; stop if the abstraction came out leaky

**6.2 `NimBus.Transport.RabbitMQ` provider package**

- `RabbitMQ.Client` 7.x. `AddRabbitMqTransport(...)` builder entry point matching `AddSqlServerMessageStore` shape
- Sessions emulated via `rabbitmq_consistent_hash_exchange` plugin + `single-active-consumer` queues, default 16 partitions per endpoint
- Scheduled enqueue via `rabbitmq_delayed_message_exchange` plugin (hard prerequisite — fail loud at startup if missing)
- Dead-letter via DLX per endpoint; mirrored into `MessageStore.UnresolvedEvents` identically to ASB DLQ
- `RabbitMqTopologyProvisioner` declares exchanges/bindings/queues/DLX/alternate-exchange (replaces topics/subscriptions/SQL filter rules)
- Health check covers connection liveness + plugin-loaded state
- Conformance suite green against a Testcontainers-managed RabbitMQ broker

**6.3 Demos, CLI, and operability**

- CrmErpDemo accepts `--Transport rabbitmq`; AppHost spins up a RabbitMQ container instead of requiring `ConnectionStrings:servicebus`
- New sample: `samples/RabbitMqOnPrem/` — minimal publisher + subscriber + RabbitMQ container, no Azure dependencies
- `nb topology apply --transport {servicebus|rabbitmq}`; `nb infra apply` skips ASB provisioning when transport is `rabbitmq`
- WebApp topology view becomes transport-aware (queues vs. subscriptions)

## Priority Matrix

| Item | Impact | Effort | Priority | Phase | Status |
|---|---|---|---|---|---|
| Transactional outbox | Critical reliability | Large | **P0** | 1 | Completed |
| DI integration | High DX | Medium | **P0** | 1 | Completed |
| Configurable retry policies | Medium DX | Small | **P1** | 1 | Completed |
| OpenTelemetry tracing | High observability | Medium | **P1** | 2 | Completed |
| In-memory transport | High testing DX | Medium | **P1** | 2 | Completed |
| Health checks | Medium ops | Small | **P1** | 2 | Completed |
| Aspire integration | High DX | Medium | **P1** | -- | Completed |
| E2E test suite | High quality | Medium | **P1** | -- | Completed |
| NuGet packages | Medium ecosystem | Small | **P4→P1** | 5 | Completed |
| Middleware pipeline | High extensibility | Medium | **P2** | 3 | Completed |
| Message scheduling SDK | High (orchestration) | Small | **P1** | 3 | Completed |
| Poison message classification | High reliability | Small | **P2** | 3 | Completed |
| Circuit breaker middleware | High resilience | Small-Medium | **P2** | 3 | Not Started |
| Inbox pattern | Medium reliability | Medium | **P2** | 3 | Not Started |
| Orchestration pattern guide | Medium DX | Small | **P3** | 3 | Not Started |
| Claim-check pattern | High (enterprise) | Medium | **P2** | 4 | Not Started |
| Request/response | High capability | Medium | **P2** | 4 | Completed |
| Failed message hook | Medium reliability | Small | **P3** | 4 | Not Started |
| WebApp enhancements | Medium ops | Large | **P3** | 4 | Mostly Complete |
| CLI operational commands | Medium ops | Medium | **P3** | -- | Completed |
| Documentation & onboarding | Medium DX | Medium | **P3** | 4 | Nearly Complete |
| Source generators | Medium DX | Medium | **P3** | 4 | Not Started |
| Message versioning | Medium contracts | Medium | **P3** | 4 | Not Started |
| Rate limiting middleware | Medium resilience | Small | **P3** | 4 | Not Started |
| Notification channels | Medium ops | Medium | **P3** | 4 | Not Started |
| Transport abstraction (RabbitMQ on-premise) | High (on-prem unlock) | Very large | **P4→P2** | 6 | In Progress (provider scaffold + topology + sender + CLI flag + on-prem sample landed; receiver loop + Testcontainers conformance pending) |
| Multi-tenant | Low | Large | **P4** | 5 | Not Started |

## What NOT to Do

- **Azure Service Bus remains the primary, recommended transport for greenfield Azure deployments.** As of 2026-05, RabbitMQ is a committed second transport for on-premise / cloud-agnostic deployments (Phase 6, [ADR-011](adr/011-rabbitmq-as-second-transport.md)) — provider scaffold, topology provisioner, sender, health check, CLI `--transport` flag, and `samples/RabbitMqOnPrem/` have landed; receiver loop + Testcontainers conformance suite + WebApp transport-aware UI remain. Do **not** add a third transport (Kafka, NATS, SQS, …) without the same level of concrete demand and a fresh ADR — the multi-transport split is sized for two providers, not n. See [`docs/transports.md`](transports.md) for the per-provider operator guide and [`docs/extensions.md`](extensions.md) for how to author a new transport package.
- **Don't chase feature parity with NServiceBus.** NServiceBus has 15+ years of development. Focus on NimBus's unique value (Resolver, WebApp, sessions) and close only the critical gaps.
- **Don't rewrite the WebApp.** It works. Enhance incrementally. The SPA + SignalR + API architecture is solid.
- **Don't build event sourcing unless there's a concrete use case.** It's a separate concern. Wolverine does it well with Marten -- recommend that for teams needing event sourcing alongside NimBus.
