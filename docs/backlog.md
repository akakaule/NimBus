# NimBus Backlog

Actionable work items extracted from the [roadmap](roadmap.md), organized by priority and status.

## Status Legend

| Status | Meaning |
|---|---|
| Completed | Implemented and merged |
| In Progress | Partially implemented |
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
| [OpenTelemetry Tracing](#opentelemetry-tracing) | 2 | Completed | `Activity`-based distributed tracing across publish/subscribe/Resolver |
| [In-Memory Transport](#in-memory-transport) | 2 | Completed | Test transport for running the full pipeline without Azure Service Bus |
| [Health Checks](#health-checks) | 2 | Completed | `IHealthCheck` implementations for Service Bus, Cosmos, Resolver lag |
| [Aspire Integration](#aspire-integration) | -- | Completed | Aspire AppHost with topology provisioning, hosted receiver, and full platform sample |
| [E2E Test Suite](#e2e-test-suite) | -- | Completed | 14+ end-to-end tests covering retry, resubmission, metadata, and lifecycle |

## P2 -- Medium

| Item | Phase | Status | Description |
|---|---|---|---|
| [Middleware Pipeline](#middleware-pipeline) | 3 | Completed | `IMessagePipelineBehavior` pipeline with 3 built-in middleware (Logging, Metrics, Validation), 15 unit tests |
| [Inbox Pattern](#inbox-pattern) | 3 | Not Started | Idempotent consumers via MessageId deduplication |
| [Saga Design](#saga-design) | 3 | Not Started | Research and prototype state machine support |

## P3 -- Lower

| Item | Phase | Status | Description |
|---|---|---|---|
| [WebApp Enhancements](#webapp-enhancements) | 4 | Mostly Complete | All done except: message flow visualization and alerting |
| [CLI Operational Commands](#cli-operational-commands) | -- | Completed | All endpoint + container commands, colored help, Spectre.Console progress |
| [Source Generators](#source-generators) | 4 | Not Started | Compile-time event type discovery replacing reflection |
| [Saga Implementation](#saga-implementation) | 4 | Not Started | Full saga persistence, timeouts, compensation, WebApp visualization |
| [Documentation & Onboarding](#documentation--onboarding) | 4 | Mostly Complete | Message flows, deferred messages, pipeline middleware docs done; getting started guide, API reference, ADRs remaining |

## P4 -- Future

| Item | Phase | Status | Description |
|---|---|---|---|
| [Transport Abstraction](#transport-abstraction) | 4 | Not Started | Evaluate `ITransport` interface for multi-transport support |
| [NuGet Packages](#nuget-packages) | 5 | Completed | NuGet packaging with SourceLink, GitHub Actions publish workflow, MIT license |
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

**Priority:** P1 | **Phase:** 2 | **Status:** Completed

`ActivitySource("NimBus")` with W3C TraceContext propagation via `Diagnostic-Id` in Service Bus `ApplicationProperties`. `NimBus.Publish` activity (Producer) on publish, `NimBus.Process` activity (Consumer) on receive with parent-child linking. `DiagnosticId` property on `IMessage` preserves trace context through outbox serialization. Tags: `messaging.system`, `messaging.destination`, `messaging.event_type`, `messaging.message_id`, `messaging.session_id`, `messaging.operation`.

Code paths:
- `src/NimBus.ServiceBus/NimBusDiagnostics.cs`
- `src/NimBus.Core/Messages/Models/Message.cs` (DiagnosticId property)
- `src/NimBus.SDK/PublisherClient.cs` (publish activity)
- `src/NimBus.ServiceBus/MessageHelper.cs` (Diagnostic-Id injection)
- `src/NimBus.ServiceBus/ServiceBusAdapter.cs` (process activity with parent extraction)
- `src/NimBus.ServiceDefaults/Extensions.cs` (AddSource + AddMeter registration)

### In-Memory Transport

**Priority:** P1 | **Phase:** 2 | **Status:** Completed

`NimBus.Testing` project providing `InMemoryMessageBus` (ISender), `InMemoryMessageContext` (IMessageContext), and `NimBusTestFixture` for running the full pipeline without Azure Service Bus. No dependency on `NimBus.ServiceBus` or Azure SDK. DI support via `AddNimBusTestTransport()`.

Code paths:
- `src/NimBus.Testing/InMemoryMessageBus.cs`
- `src/NimBus.Testing/InMemoryMessageContext.cs`
- `src/NimBus.Testing/InMemorySessionState.cs`
- `src/NimBus.Testing/InMemoryDeliveryResult.cs`
- `src/NimBus.Testing/NimBusTestFixture.cs`
- `src/NimBus.Testing/Extensions/ServiceCollectionExtensions.cs`

### Health Checks

**Priority:** P1 | **Phase:** 2 | **Status:** Completed

`IHealthCheck` implementations for Service Bus (`ServiceBusClient.IsClosed`), Cosmos DB (`ReadAccountAsync`), and Resolver lag (heartbeat age with configurable thresholds: Healthy < 5min, Degraded 5-15min, Unhealthy > 15min). All tagged `"ready"` and exposed via `/ready` endpoint. Registration via `AddServiceBusHealthCheck()`, `AddCosmosDbHealthCheck()`, `AddResolverLagCheck()`.

Code paths:
- `src/NimBus.ServiceBus/HealthChecks/ServiceBusHealthCheck.cs`
- `src/NimBus.ServiceBus/HealthChecks/ServiceBusHealthCheckExtensions.cs`
- `src/NimBus.MessageStore/HealthChecks/CosmosDbHealthCheck.cs`
- `src/NimBus.MessageStore/HealthChecks/ResolverLagHealthCheck.cs`
- `src/NimBus.MessageStore/HealthChecks/HealthCheckExtensions.cs`
- `src/NimBus.ServiceDefaults/Extensions.cs` (/ready endpoint)
- `src/NimBus.WebApp/Startup.cs` (health check registration)

### Middleware Pipeline

**Priority:** P2 | **Phase:** 3 | **Status:** Completed

`IMessagePipelineBehavior` with `Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct)`. Pipeline wired into `MessageHandler` via `MessagePipeline`, resolved from DI by `AddNimBusSubscriber()`. Three built-in middleware: `LoggingMiddleware` (timing + metadata), `MetricsMiddleware` (OpenTelemetry counters/histograms), `ValidationMiddleware` (dead-letters invalid messages with `MessageAlreadyDeadLetteredException`).

Also completed: removed `NimBus.Core.Logging` abstraction — all logging uses `Microsoft.Extensions.Logging`. Handler signature simplified to `Handle(T message, IEventHandlerContext context, CancellationToken ct)`. Separated `DeferredProcessor` from `StrictMessageHandler` to dedicated subscriber service. Metrics renamed from `dis.*` to `nimbus.*`.

Code paths:
- `src/NimBus.Core/Pipeline/LoggingMiddleware.cs`
- `src/NimBus.Core/Pipeline/MetricsMiddleware.cs`
- `src/NimBus.Core/Pipeline/ValidationMiddleware.cs`
- `src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs`
- `src/NimBus.Core/Extensions/MessagePipeline.cs`
- `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs` (pipeline wiring)
- `docs/pipeline-middleware.md`
- `tests/NimBus.Core.Tests/BuiltInMiddlewareTests.cs` (15 tests)

### Inbox Pattern

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Idempotent consumers via `MessageId` deduplication. Track processed IDs in Cosmos DB or SQL with configurable TTL. Consumers opt in via configuration. Pairs with the outbox for exactly-once semantics.

### Saga Design

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Design-only phase. Research state machine DSL (inspired by MassTransit Automatonymous), saga persistence in Cosmos DB, timeout scheduling via Service Bus scheduled messages, compensation actions.

### WebApp Enhancements

**Priority:** P3 | **Phase:** 4 | **Status:** Mostly Complete

- [x] Dashboard metrics: time-series area chart with gap-filling, KPI summary cards, event-type-level breakdown
- [x] Failed message insights: error pattern grouping and Insights page
- [x] Audit log search: filterable audit search API and UI
- [x] Bulk operations: subscription purge, delete by status, skip messages, delete by destination, copy endpoint data, delete all events -- accordion-organized admin with Recovery/Cleanup/Infrastructure/Danger Zone sections
- [x] Comment/audit section on event details with user info from `/api/me`
- [x] Reprocess deferred button and API for orphaned deferred messages
- [x] Delete button on event details for failed/deadlettered/unsupported events
- [x] Grouped by Error view on endpoint details with bulk Resubmit All / Skip All per error pattern
- [x] EnumMemberModelBinder for correct enum query string binding (fixed metrics time scale)
- [x] Millisecond precision in all datetime displays
- [ ] Message flow visualization (trace a message through its full lifecycle)
- [ ] Alerting (webhook/email for failures, dead-letters, session blocks)

### Source Generators

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Replace reflection-based event type discovery with compile-time source generators. Strongly-typed `NimBusOptions` configuration with validation. Better error messages for misconfiguration.

### Saga Implementation

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Based on Phase 3 design: saga persistence in Cosmos DB, timeout scheduling, compensation actions, saga visualization in WebApp.

### Documentation & Onboarding

**Priority:** P3 | **Phase:** 4 | **Status:** Mostly Complete

- [x] Sample applications: Aspire Pub/Sub sample with Publisher, Subscriber (with DeferredProcessorService and middleware), Provisioner, ResolverWorker
- [x] CI/CD documentation: GitHub Actions and Azure DevOps deploy pipelines using `nb` CLI
- [x] README updates: local development (Aspire) and CI/CD setup instructions
- [x] Message flow documentation: `docs/message-flows.md` with 10 flow diagrams covering all message types
- [x] Deferred message processing guide: `docs/deferred-messages.md` with Mermaid sequence diagrams
- [x] Pipeline middleware documentation: `docs/pipeline-middleware.md` with patterns, built-in middleware, and API reference
- [ ] Getting started guide
- [ ] SDK API reference
- [ ] Architecture decision records (ADRs)
- [ ] Migration guide from MassTransit/NServiceBus

### Transport Abstraction

**Priority:** P4 | **Phase:** 4 | **Status:** Not Started

Evaluate `ITransport` interface for multi-transport support. Recommendation: start with in-memory for testing (Phase 2.2), only abstract further if there's concrete demand for RabbitMQ/Kafka.

### NuGet Packages

**Priority:** P4 | **Phase:** 5 | **Status:** Completed

NuGet packaging with SourceLink, GitHub Actions publish workflow (`nuget-publish.yml`), and MIT license. Five consumer-facing projects marked as packable: Abstractions, Core, ServiceBus, SDK, CommandLine. Package metadata configured in `Directory.Build.props`.

Code paths:
- `Directory.Build.props` (NuGet metadata and SourceLink config)
- `.github/workflows/nuget-publish.yml` (publish workflow)
- `LICENSE` (MIT)

### Multi-Tenant Support

**Priority:** P4 | **Phase:** 5 | **Status:** Not Started

Tenant-isolated topics/subscriptions with per-tenant configuration and routing.

### Event Sourcing

**Priority:** P4 | **Phase:** 5 | **Status:** Not Started

Optional integration with Marten or a custom event store. Separate `NimBus.EventSourcing` package. Only pursue if there's a concrete use case.

### Aspire Integration

**Priority:** P1 | **Phase:** -- | **Status:** Completed

.NET Aspire AppHost integration with automatic Service Bus topology provisioning, hosted session processor support, and a full platform sample demonstrating the complete message flow.

- `NimBusReceiverHostedService` for hosting session processors in Aspire
- `ServiceBusTopologyProvisioner` made public with connection-string constructor
- Provisioner console app for standalone topology provisioning
- ResolverWorker hosting ResolverService
- AppHost wiring: Publisher → StorefrontEndpoint → AspireSampleEndpoint

Code paths:
- `samples/NimBus.Aspire/`
- `src/NimBus.ServiceBus/Hosting/NimBusReceiverHostedService.cs`
- `src/NimBus.ServiceBus/Provisioning/ServiceBusTopologyProvisioner.cs`

### E2E Test Suite

**Priority:** P1 | **Phase:** -- | **Status:** Completed

14+ end-to-end tests covering retry backoff strategies (linear, exponential, max delay cap), exception-based retry rules, retry count propagation, resubmission flow, session FIFO ordering, dead-letter lifecycle observer, pipeline behavior error handling, heartbeat messages, response metadata integrity, and batch edge cases.

Code paths:
- `tests/NimBus.EndToEnd.Tests/`

### CLI Operational Commands

**Priority:** P3 | **Phase:** -- | **Status:** Completed

Expansion of the `nb` CLI with operational management commands using Spectre.Console for rich terminal output.

- Endpoint management: delete sessions, purge subscriptions (with state/date filters), remove deprecated subscriptions/rules, topic/subscription/rule tree visualization
- Container operations: delete documents, resubmit/skip messages, purge by destination, copy data between Cosmos DB instances
- Enhanced CLI UX: colored help text generator, progress spinners, global connection string options (`-sbc`, `-dbc`)

Code paths:
- `src/NimBus.CommandLine/Endpoint.cs`
- `src/NimBus.CommandLine/Container.cs`
- `src/NimBus.CommandLine/CommandRunner.cs`
- `src/NimBus.CommandLine/ColoredHelpTextGenerator.cs`
- `src/NimBus.CommandLine/Models/`
