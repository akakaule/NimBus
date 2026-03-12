# NimBus Roadmap

## Competitive Position

NimBus is a mature, Azure-native event-driven integration platform with strong differentiators: session-based ordered processing, centralized Resolver with full audit trail, management WebApp with resubmit/skip, and declarative topology provisioning. However, compared to mature open-source frameworks (MassTransit, Wolverine, NServiceBus, CAP, Rebus, Brighter), NimBus has gaps that affect reliability, developer experience, and observability. This roadmap closes those gaps while preserving NimBus's unique strengths.

| | NimBus Strength | Competitor Advantage |
|---|---|---|
| **Ordering** | Session-based FIFO with deferred replay | Wolverine has partitioned sequential; most others lack this |
| **Audit trail** | Centralized Resolver + Cosmos projections | Only NServiceBus (ServiceControl) has comparable |
| **Ops UI** | WebApp with resubmit/skip | Only NServiceBus (ServicePulse) and CAP (dashboard) |
| **Reliability** | Transactional outbox (Phase 1) | All mature frameworks have transactional outbox |
| **Observability** | Metrics only, no distributed tracing | All competitors have OpenTelemetry Activity tracing |
| **DX** | DI integration (Phase 1) | All competitors integrate with MS DI natively |
| **Extensibility** | Extension framework, no middleware pipeline | NServiceBus (behaviors), MassTransit (filters), Brighter (decorators) |
| **Testing** | Requires real Service Bus | MassTransit, Wolverine, Rebus have in-memory transports |
| **Workflows** | Continuation pattern (limited) | NServiceBus, MassTransit, Wolverine have full saga/state machines |
| **Transport** | Azure Service Bus only | Most support RabbitMQ, Kafka, SQL, in-memory |

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

### Phase 2: Observability & Testing (Q3 2026)

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

### Phase 3: Extensibility & Patterns (Q4 2026)

Goal: Enable cross-cutting concerns and more complex workflows.

**3.1 Middleware Pipeline**

Allow injecting cross-cutting behavior (logging, auth, validation, metrics, error enrichment) into the message processing pipeline without modifying framework code.

- Define `IMessageMiddleware` with `Task InvokeAsync(IMessageContext context, MessageDelegate next)`
- Chain middleware in `StrictMessageHandler` before handler invocation
- Support attribute-based and registration-based middleware
- Built-in middleware: `LoggingMiddleware`, `ValidationMiddleware`, `MetricsMiddleware`

Note: The extension framework (`INimBusExtension`, `IMessageLifecycleObserver`, `IMessagePipelineBehavior`) from Phase 1 provides hooks for this. The middleware pipeline builds on that foundation.

**3.2 Inbox Pattern (Idempotent Consumers)**

Paired with the outbox for exactly-once semantics on the consumer side.

- Track processed `MessageId` values in a deduplication store (Cosmos DB or SQL)
- Check before handler invocation; skip if already processed
- Configurable TTL for deduplication entries
- Optional -- consumers opt in via configuration

**3.3 Saga / State Machine Support (Design Phase)**

NimBus's continuation pattern is a limited form of saga. A full saga framework would enable multi-step workflows with timeout handling and compensation.

Design-only in this phase:
- State machine DSL (inspired by MassTransit's Automatonymous)
- Saga state persistence (Cosmos DB)
- Timeout scheduling via Service Bus scheduled messages
- Compensation actions on failure

---

### Phase 4: Platform Maturity (H1 2027)

Goal: Production hardening, developer experience polish, and ecosystem growth.

**4.1 WebApp Enhancements**

- Message flow visualization: trace a message through its full lifecycle
- Dashboard metrics: throughput, latency percentiles, error rates per endpoint
- Bulk operations: resubmit/skip multiple failed messages at once
- Alerting: webhook/email notifications for failed messages, dead-letters, or session blocks

**4.2 SDK Developer Experience**

- Source generators: replace reflection-based event type discovery with compile-time source generators
- Strongly-typed configuration: `NimBusOptions` pattern with validation
- Better error messages: actionable errors when misconfigured

**4.3 Saga / State Machine Implementation**

Based on Phase 3 design work:
- Saga persistence in Cosmos DB
- Saga timeout scheduling
- Compensation actions
- Saga visualization in WebApp

**4.4 Transport Abstraction (Evaluate)**

Evaluate whether transport abstraction is worth the complexity:
- Full abstraction: `ITransport` interface with Azure Service Bus, RabbitMQ, in-memory implementations
- Minimal abstraction: keep Azure Service Bus as primary, add in-memory for testing only (Phase 2.2)
- Recommendation: start with in-memory for testing, only abstract further if there's concrete demand

**4.5 Documentation & Onboarding**

- Getting started guide
- SDK API reference
- Architecture decision records (ADRs)
- Sample applications (e-commerce, IoT)
- Migration guide from MassTransit/NServiceBus

---

### Phase 5: Ecosystem & Scale (H2 2027+)

Goal: If NimBus is to be open-sourced or adopted beyond its current org.

**5.1 NuGet Package Publishing**

- Split SDK into publishable NuGet packages
- Semantic versioning
- Public API surface review and stability guarantees

**5.2 Multi-Tenant Support**

- Tenant-isolated topics/subscriptions
- Per-tenant configuration and routing

**5.3 Event Sourcing Integration (Optional)**

- If there's demand, integrate with Marten or a custom event store
- Keep as a separate `NimBus.EventSourcing` package

## Priority Matrix

| Item | Impact | Effort | Priority | Phase |
|---|---|---|---|---|
| Transactional outbox | Critical reliability | Large | **P0** | 1 |
| DI integration | High DX | Medium | **P0** | 1 |
| Configurable retry policies | Medium DX | Small | **P1** | 1 |
| OpenTelemetry tracing | High observability | Medium | **P1** | 2 |
| In-memory transport | High testing DX | Medium | **P1** | 2 |
| Health checks | Medium ops | Small | **P1** | 2 |
| Middleware pipeline | High extensibility | Medium | **P2** | 3 |
| Inbox pattern | Medium reliability | Medium | **P2** | 3 |
| Saga design | High capability | Large | **P2** | 3 |
| WebApp enhancements | Medium ops | Large | **P3** | 4 |
| Source generators | Medium DX | Medium | **P3** | 4 |
| Saga implementation | High capability | Very large | **P3** | 4 |
| Transport abstraction | Low-Medium | Very large | **P4** | 4 |
| Multi-tenant | Low | Large | **P4** | 5 |

## What NOT to Do

- **Don't abstract transports prematurely.** Azure Service Bus is NimBus's strength. Adding RabbitMQ/Kafka support before there's demand adds complexity without value. In-memory for testing is sufficient.
- **Don't chase feature parity with NServiceBus.** NServiceBus has 15+ years of development. Focus on NimBus's unique value (Resolver, WebApp, sessions) and close only the critical gaps.
- **Don't rewrite the WebApp.** It works. Enhance incrementally. The SPA + SignalR + API architecture is solid.
- **Don't build event sourcing unless there's a concrete use case.** It's a separate concern. Wolverine does it well with Marten -- recommend that for teams needing event sourcing alongside NimBus.
