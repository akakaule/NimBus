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
| [Message Scheduling SDK](#message-scheduling-sdk) | 3 | Completed | First-class scheduled and cancellable message delivery via Service Bus |
| [OpenTelemetry Tracing](#opentelemetry-tracing) | 2 | Completed | `Activity`-based distributed tracing across publish/subscribe/Resolver |
| [In-Memory Transport](#in-memory-transport) | 2 | Completed | Test transport for running the full pipeline without Azure Service Bus |
| [Health Checks](#health-checks) | 2 | Completed | `IHealthCheck` implementations for Service Bus, Cosmos, Resolver lag |
| [Aspire Integration](#aspire-integration) | -- | Completed | Aspire AppHost with topology provisioning, hosted receiver, and full platform sample |
| [E2E Test Suite](#e2e-test-suite) | -- | Completed | 14+ end-to-end tests covering retry, resubmission, metadata, and lifecycle |

## P2 -- Medium

| Item | Phase | Status | Description |
|---|---|---|---|
| [Middleware Pipeline](#middleware-pipeline) | 3 | Completed | `IMessagePipelineBehavior` pipeline with 3 built-in middleware (Logging, Metrics, Validation), 15 unit tests |
| [Poison Message Classification](#poison-message-classification) | 3 | Completed | Transient vs permanent failure detection with immediate dead-letter for unrecoverable errors |
| [Circuit Breaker Middleware](#circuit-breaker-middleware) | 3 | Not Started | Pause processing when downstream dependencies are failing systemically |
| [Inbox Pattern](#inbox-pattern) | 3 | Not Started | Idempotent consumers via MessageId deduplication |
| [Claim-Check Pattern](#claim-check-pattern) | 4 | Not Started | Large payload offload to Azure Blob Storage |
| [Request/Response](#requestresponse) | 4 | Completed | Synchronous request/response over the bus with session-based reply correlation |

## P3 -- Lower

| Item | Phase | Status | Description |
|---|---|---|---|
| [WebApp Enhancements](#webapp-enhancements) | 4 | Mostly Complete | All done except: alerting |
| [CLI Operational Commands](#cli-operational-commands) | -- | Completed | All endpoint + container commands, colored help, Spectre.Console progress |
| [SessionKey Attribute](#sessionkey-attribute) | -- | Completed | Declarative `[SessionKey(nameof(Prop))]` replacing `GetSessionId()` overrides |
| [Identity Extension](#identity-extension) | -- | Completed | `NimBus.Extensions.Identity` for username/password auth with email verification |
| [Product Website](#product-website) | -- | Completed | GitHub Pages landing site with architecture diagram, code examples, comparison |
| [Failed Message Hook](#failed-message-hook) | 4 | Not Started | Application-level last-chance handler before dead-lettering |
| [Source Generators](#source-generators) | 4 | Not Started | Compile-time event type discovery replacing reflection |
| [Message Versioning](#message-versioning) | 4 | Not Started | Schema evolution with polymorphic dispatch and version attributes |
| [Orchestration Pattern Guide](#orchestration-pattern-guide) | 3 | Not Started | Documentation and sample for multi-step workflow orchestration (ADR-009) |
| [Rate Limiting Middleware](#rate-limiting-middleware) | 4 | Not Started | Throttle message consumption to protect downstream services |
| [Notification Channels](#notification-channels) | 4 | Not Started | Webhook, Teams, and email channels for production alerting |
| [Documentation & Onboarding](#documentation--onboarding) | 4 | Mostly Complete | All done except: migration guide from MassTransit/NServiceBus |

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

### Orchestration Pattern Guide

**Priority:** P3 | **Phase:** 3 | **Status:** Not Started

Documentation and sample showing how to build multi-step workflow orchestration services using NimBus's messaging primitives. Sagas are implemented as application-level services, not a NimBus framework feature (see [ADR-009](adr/009-orchestration-via-application-services.md)).

- Orchestration pattern documentation (`docs/orchestration.md`)
- Sample orchestrator service in the Aspire Pub/Sub sample
- Timeout patterns using `ScheduleMessage` / `CancelScheduledMessage`
- Compensation patterns via published events

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
- [x] Message flow visualization: timeline view in Event Details showing full message lifecycle
- [ ] Alerting (webhook/email for failures, dead-letters, session blocks)

### Source Generators

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Replace reflection-based event type discovery with compile-time source generators. Strongly-typed `NimBusOptions` configuration with validation. Better error messages for misconfiguration.

### Documentation & Onboarding

**Priority:** P3 | **Phase:** 4 | **Status:** Mostly Complete

- [x] Sample applications: Aspire Pub/Sub sample with Publisher, Subscriber (with DeferredProcessorService and middleware), Provisioner, ResolverWorker
- [x] CI/CD documentation: GitHub Actions and Azure DevOps deploy pipelines using `nb` CLI
- [x] README updates: local development (Aspire) and CI/CD setup instructions
- [x] Message flow documentation: `docs/message-flows.md` with 10 flow diagrams covering all message types
- [x] Deferred message processing guide: `docs/deferred-messages.md` with Mermaid sequence diagrams
- [x] Pipeline middleware documentation: `docs/pipeline-middleware.md` with patterns, built-in middleware, and API reference
- [x] Getting started guide: `docs/getting-started.md`
- [x] SDK API reference: `docs/sdk-api-reference.md`
- [x] Architecture decision records: 8 ADRs in `docs/adr/`
- [x] CLI reference: `docs/cli.md` with full command documentation
- [x] Azure Functions hosting guide: `docs/azure-functions-hosting.md`
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

### Message Scheduling SDK

**Priority:** P1 | **Phase:** 3 | **Status:** Not Started

First-class message scheduling exposing Azure Service Bus's native `ScheduledEnqueueTimeUtc`. Enables workflow timeouts in orchestration services.

- `ISender.ScheduleMessage(IMessage, DateTimeOffset)` returning cancellable `long sequenceNumber`
- `ISender.CancelScheduledMessage(long sequenceNumber)`
- Outbox integration for scheduled messages
- Replaces the limited `messageEnqueueDelay` (minutes-only) parameter

Reference: MassTransit `IMessageScheduler`, Rebus `bus.Defer()`.

### Poison Message Classification

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Distinguish transient failures (timeouts, 503s, rate limits) from permanent failures (validation, deserialization, authorization). Permanent failures dead-letter immediately without consuming retry budget.

- `IPermanentFailureClassifier` interface with default implementation for common exception types
- Configurable per event type via `DefaultRetryPolicyProvider`
- Extends existing retry pipeline in `StrictMessageHandler`
- Two-phase retries: in-memory immediate retries for transient errors, delayed redelivery for systemic issues

Reference: NServiceBus two-phase recoverability.

### Circuit Breaker Middleware

**Priority:** P2 | **Phase:** 3 | **Status:** Not Started

Pause message processing when a downstream dependency is systematically failing. Prevents cascading dead-lettering when every message hits the same broken service.

- `CircuitBreakerMiddleware` implementing `IMessagePipelineBehavior`
- Uses Polly V8 resilience pipeline
- Configurable failure threshold, break duration, half-open test count
- When open: messages are abandoned (returned to queue), not dead-lettered
- Per-endpoint or per-event-type configuration

Reference: Wolverine per-endpoint circuit breaker, Brighter `[UseResiliencePipeline]`.

### Claim-Check Pattern

**Priority:** P2 | **Phase:** 4 | **Status:** Not Started

Offload large message payloads (>256KB) to Azure Blob Storage, passing only a reference through Service Bus. Essential for messages with binary attachments or large JSON.

- `ClaimCheckMiddleware` implementing `IMessagePipelineBehavior`
- Publisher side: serialize payload to Blob Storage, replace body with blob URI
- Consumer side: detect claim-check reference, retrieve payload transparently
- Configurable size threshold
- Package as `NimBus.Extensions.ClaimCheck`

Reference: NServiceBus DataBus / `[DataBusProperty]`, Azure Architecture Center claim-check pattern.

### Request/Response

**Priority:** P2 | **Phase:** 4 | **Status:** Not Started

Synchronous request/response over the bus. Enables query patterns (e.g., "get order status") between services without building separate HTTP APIs.

- `ISender.Request<TRequest, TResponse>(TRequest, TimeSpan timeout)` returning `Task<TResponse>`
- Uses Azure Service Bus reply-to queue with session-based correlation
- Timeout handling with `OperationCanceledException`

Reference: MassTransit `IRequestClient<T>`.

### Failed Message Hook

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Application-level last-chance handler before dead-lettering. After retries are exhausted, invoke an `IFailed<T>` handler for custom recovery, enriched diagnostics, or re-routing.

- `IFailedMessageHandler<T>` interface invoked after retry exhaustion, before dead-letter
- Handler can: log enriched diagnostics, send to alternative endpoint, modify and retry, or allow dead-letter
- Registered via `builder.AddFailedHandler<OrderPlaced, OrderPlacedFailedHandler>()`

Reference: Rebus `IFailed<T>`.

### Message Versioning

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Support evolving message contracts over time without breaking existing consumers. The #1 source of production incidents in long-lived messaging systems.

- Additive nullable fields (works with JSON serialization; needs documentation and testing)
- `[MessageVersion]` attribute for explicit version tracking
- Inheritance-based polymorphic dispatch: `IEventHandler<OrderPlacedV1>` also receives `OrderPlacedV2` messages
- Compile-time schema compatibility checks via source generators

Reference: NServiceBus evolving contracts, polymorphic message dispatch.

### Rate Limiting Middleware

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Throttle message consumption rate to protect downstream services and external API quotas.

- `RateLimitingMiddleware` implementing `IMessagePipelineBehavior`
- Uses `System.Threading.RateLimiting` (token bucket, sliding window, fixed window)
- Configurable per event type or per endpoint
- When limit exceeded: messages are abandoned (returned to queue), not dead-lettered

### Notification Channels

**Priority:** P3 | **Phase:** 4 | **Status:** Not Started

Production-ready notification channels extending the existing `NimBus.Extensions.Notifications` framework.

- Webhook channel (HTTP POST with configurable payload template)
- Microsoft Teams channel (incoming webhook connector)
- Email channel (SendGrid or SMTP)
- Severity-based routing: route Critical to all channels, Warning to webhook only
- Rate limiting and batching to prevent notification storms
- Completes the alerting feature (4.1)

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
- Function App stop/start during deployment to prevent file lock errors

Code paths:
- `src/NimBus.CommandLine/Endpoint.cs`
- `src/NimBus.CommandLine/Container.cs`
- `src/NimBus.CommandLine/CommandRunner.cs`
- `src/NimBus.CommandLine/AppDeploymentService.cs`
- `src/NimBus.CommandLine/ColoredHelpTextGenerator.cs`
- `src/NimBus.CommandLine/Models/`

### SessionKey Attribute

**Priority:** P3 | **Phase:** -- | **Status:** Completed

Declarative `[SessionKey(nameof(OrderId))]` attribute as an alternative to overriding `GetSessionId()`. Events become clean DTOs without infrastructure method overrides. The base `Event.GetSessionId()` reads the attribute via reflection, falling back to `Guid.NewGuid()`. Existing overrides still take precedence (backward compatible).

Code paths:
- `src/NimBus.Abstractions/Events/SessionKeyAttribute.cs`
- `src/NimBus.Abstractions/Events/Event.cs`
- `src/NimBus.Abstractions/Events/EventType.cs` (SessionKeyProperty metadata)

### Identity Extension

**Priority:** P3 | **Phase:** -- | **Status:** Completed

`NimBus.Extensions.Identity` — opt-in ASP.NET Core Identity extension for username/password authentication with email verification. SQL Server-backed, separate from the core WebApp. Supports three auth modes: Identity-only, Entra ID-only, or dual (both). Claims transformation maps Identity users to existing NimBus authorization model without changing `EndpointAuthorizationService`.

Code paths:
- `src/NimBus.Extensions.Identity/`
- `src/NimBus.WebApp/Startup.cs` (Identity detection and auth mode routing)

### Product Website

**Priority:** P3 | **Phase:** -- | **Status:** Completed

Static landing page at `site/index.html` deployed via GitHub Pages. Azure blue color scheme, SVG architecture diagram, code examples, session ordering visualization, competitor comparison table. Auto-deploys via `.github/workflows/pages.yml`.

Live at: https://akakaule.github.io/NimBus/

Code paths:
- `site/index.html`
- `site/banner.png`
- `.github/workflows/pages.yml`
