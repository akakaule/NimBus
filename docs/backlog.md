# NimBus Backlog

Actionable work items extracted from the [roadmap](roadmap.md), organized by priority and status.

## Status Legend

| Status | Meaning |
|---|---|
| Completed | Implemented and merged |
| Not Started | Planned, not yet in progress |

## P0 -- Critical

| Item | Phase | Status | Description |
|---|---|---|---|
| [Transactional Outbox](#transactional-outbox) | 1 | Completed | Outbox pattern preventing message loss on crash between DB commit and send |
| [DI Integration](#di-integration) | 1 | Completed | Standard `IServiceCollection` registration replacing factory-based handlers |

## P1 -- High

| Item | Phase | Status | Description |
|---|---|---|---|
| [Configurable Retry Policies](#configurable-retry-policies) | 1 | Completed | Per-event-type and exception-based retry with backoff strategies |
| [OpenTelemetry Tracing](#opentelemetry-tracing) | 2 | Not Started | `Activity`-based distributed tracing across publish/subscribe/Resolver |
| [In-Memory Transport](#in-memory-transport) | 2 | Not Started | Test transport for running the full pipeline without Azure Service Bus |
| [Health Checks](#health-checks) | 2 | Not Started | `IHealthCheck` implementations for Service Bus, Cosmos, Resolver lag |

## P2 -- Medium

| Item | Phase | Status | Description |
|---|---|---|---|
| [Middleware Pipeline](#middleware-pipeline) | 3 | Not Started | `IMessageMiddleware` chain for cross-cutting concerns |
| [Inbox Pattern](#inbox-pattern) | 3 | Not Started | Idempotent consumers via MessageId deduplication |
| [Saga Design](#saga-design) | 3 | Not Started | Research and prototype state machine support |

## P3 -- Lower

| Item | Phase | Status | Description |
|---|---|---|---|
| [WebApp Enhancements](#webapp-enhancements) | 4 | Not Started | Flow visualization, dashboard metrics, bulk ops, alerting |
| [Source Generators](#source-generators) | 4 | Not Started | Compile-time event type discovery replacing reflection |
| [Saga Implementation](#saga-implementation) | 4 | Not Started | Full saga persistence, timeouts, compensation, WebApp visualization |
| [Documentation & Onboarding](#documentation--onboarding) | 4 | Not Started | Getting started guide, API reference, ADRs, samples |

## P4 -- Future

| Item | Phase | Status | Description |
|---|---|---|---|
| [Transport Abstraction](#transport-abstraction) | 4 | Not Started | Evaluate `ITransport` interface for multi-transport support |
| [NuGet Packages](#nuget-packages) | 5 | Not Started | Publishable NuGet packages with semantic versioning |
| [Multi-Tenant Support](#multi-tenant-support) | 5 | Not Started | Tenant-isolated topics, subscriptions, and routing |
| [Event Sourcing](#event-sourcing) | 5 | Not Started | Optional Marten/custom event store integration |

---

## Item Details

### Transactional Outbox

**Priority:** P0 | **Phase:** 1 | **Status:** Completed

Prevents message loss when a process crashes between committing a database change and sending the Service Bus message. `OutboxSender` decorates `ISender` to write messages to an outbox table. `OutboxDispatcher` polls and forwards to the real sender.

Code paths:
- `src/NimBus.Core/Outbox/IOutbox.cs`
- `src/NimBus.Core/Outbox/IOutboxCleanup.cs`
- `src/NimBus.Core/Outbox/OutboxMessage.cs`
- `src/NimBus.Core/Outbox/OutboxSender.cs`
- `src/NimBus.Core/Outbox/OutboxDispatcher.cs`
- `src/NimBus.Outbox.SqlServer/SqlServerOutbox.cs`
- `src/NimBus.Outbox.SqlServer/SqlServerOutboxOptions.cs`
- `src/NimBus.Outbox.SqlServer/ServiceCollectionExtensions.cs`

### DI Integration

**Priority:** P0 | **Phase:** 1 | **Status:** Completed

Standard `IServiceCollection` extension methods for publisher and subscriber registration. Handler resolution via `IServiceProvider.GetRequiredService<IEventHandler<T>>()`. Existing factory API preserved as `[Obsolete]`.

Code paths:
- `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`
- `src/NimBus.SDK/Extensions/NimBusPublisherOptions.cs`
- `src/NimBus.SDK/Extensions/NimBusSubscriberOptions.cs`
- `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`
- `src/NimBus.SDK/Hosting/OutboxDispatcherHostedService.cs`
- `src/NimBus.SDK/Hosting/OutboxDispatcherSender.cs`

### Configurable Retry Policies

**Priority:** P1 | **Phase:** 1 | **Status:** Completed

Configuration-driven retry policies replacing the hardcoded `RetryDefinitions`. Supports Fixed, Linear, and Exponential backoff strategies. Policies can be set per event type, per exception type, or as a default. Old `RetryDefinitions` implements `IRetryPolicyProvider` as a backward-compatible bridge.

Code paths:
- `src/NimBus.Core/Messages/RetryPolicy.cs`
- `src/NimBus.Core/Messages/IRetryPolicyProvider.cs`
- `src/NimBus.Core/Messages/DefaultRetryPolicyProvider.cs`
- `src/NimBus.Core/Messages/RetryDefinitions.cs` (obsolete bridge)

### OpenTelemetry Tracing

**Priority:** P1 | **Phase:** 2 | **Status:** Not Started

Add `ActivitySource` and W3C TraceContext propagation. Traces should flow from `PublisherClient.Publish` through Service Bus to `ServiceBusAdapter.Handle` and into the Resolver. Enrich with NimBus-specific tags: EventTypeId, EndpointId, SessionId, MessageType.

Target files:
- `src/NimBus.ServiceBus/ServiceBusAdapter.cs`
- `src/NimBus.SDK/PublisherClient.cs`
- `src/NimBus.ServiceDefaults/Extensions.cs`

### In-Memory Transport

**Priority:** P1 | **Phase:** 2 | **Status:** Not Started

`InMemoryServiceBusClient` that mimics topic/subscription routing with session support. Enables unit tests without Azure credentials and fast CI/CD. Likely a new `src/NimBus.Testing/` project.

### Health Checks

**Priority:** P1 | **Phase:** 2 | **Status:** Not Started

Standard `IHealthCheck` implementations: Service Bus connectivity, Cosmos DB connectivity, Resolver lag (time since last processed message). Register via `services.AddNimBusHealthChecks()`.

### Middleware Pipeline

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

`IMessageMiddleware` with `Task InvokeAsync(IMessageContext context, MessageDelegate next)`. Chained in `StrictMessageHandler` before handler invocation. Builds on the extension framework (`IMessagePipelineBehavior`) from Phase 1. Built-in: `LoggingMiddleware`, `ValidationMiddleware`, `MetricsMiddleware`.

Target files:
- New: `src/NimBus.Core/Pipeline/IMessageMiddleware.cs`
- Modified: `src/NimBus.Core/Messages/StrictMessageHandler.cs`

### Inbox Pattern

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Idempotent consumers via `MessageId` deduplication. Track processed IDs in Cosmos DB or SQL with configurable TTL. Consumers opt in via configuration. Pairs with the outbox for exactly-once semantics.

### Saga Design

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Design-only phase. Research state machine DSL (inspired by MassTransit Automatonymous), saga persistence in Cosmos DB, timeout scheduling via Service Bus scheduled messages, compensation actions.

### WebApp Enhancements

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

- Message flow visualization (trace a message through its full lifecycle)
- Dashboard metrics (throughput, latency percentiles, error rates per endpoint)
- Bulk operations (resubmit/skip multiple failed messages)
- Alerting (webhook/email for failures, dead-letters, session blocks)

### Source Generators

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Replace reflection-based event type discovery with compile-time source generators. Strongly-typed `NimBusOptions` configuration with validation. Better error messages for misconfiguration.

### Saga Implementation

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Based on Phase 3 design: saga persistence in Cosmos DB, timeout scheduling, compensation actions, saga visualization in WebApp.

### Documentation & Onboarding

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Getting started guide, SDK API reference, architecture decision records (ADRs), sample applications (e-commerce, IoT), migration guide from MassTransit/NServiceBus.

### Transport Abstraction

**Priority:** P4 | **Phase:** 4 | **Status:** Not Started

Evaluate `ITransport` interface for multi-transport support. Recommendation: start with in-memory for testing (Phase 2.2), only abstract further if there's concrete demand for RabbitMQ/Kafka.

### NuGet Packages

**Priority:** P4 | **Phase:** 5 | **Status:** Not Started

Split SDK into publishable NuGet packages with semantic versioning and public API surface review.

### Multi-Tenant Support

**Priority:** P4 | **Phase:** 5 | **Status:** Not Started

Tenant-isolated topics/subscriptions with per-tenant configuration and routing.

### Event Sourcing

**Priority:** P4 | **Phase:** 5 | **Status:** Not Started

Optional integration with Marten or a custom event store. Separate `NimBus.EventSourcing` package. Only pursue if there's a concrete use case.
