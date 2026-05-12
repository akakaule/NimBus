# NimBus Features

This document is a concise catalog of the current NimBus feature set, grouped into **core platform features** and **extensions**.

## Core platform features

| Area | Feature | Brief description | Primary packages/services |
|---|---|---|---|
| Messaging model | Event-driven pub/sub | Endpoints publish and consume typed events through Azure Service Bus topics/subscriptions. | `NimBus.SDK`, `NimBus.ServiceBus` |
| Ordering | Session-based FIFO processing | Messages are grouped by `SessionId` to guarantee ordered handling per business key. | `NimBus.Core`, `NimBus.ServiceBus` |
| Developer API | Typed publisher/subscriber API | DI registrations for publishers, subscribers, handlers, and receiver hosting. | `NimBus.SDK` |
| Event modeling | SessionKey + metadata attributes | `[SessionKey]`, validation, and description attributes for ordering and schema/documentation. | `NimBus.Core` |
| Reliability | Retry policies with backoff | Per-event and exception-pattern retry policies with fixed/linear/exponential strategies. | `NimBus.Core` |
| Reliability | Permanent failure classification | Classify unrecoverable exceptions and dead-letter immediately without retry budget use. | `NimBus.Core` |
| Reliability | Session blocking + deferral | When a message fails, later messages in the same session are deferred to preserve order. | `NimBus.Core`, `NimBus.ServiceBus` |
| Recovery | Deferred replay | Deferred messages are re-published in FIFO order after resubmit/retry/skip unblocks the session. | `NimBus.ServiceBus` |
| Recovery | Resubmit/skip flows | Failed events can be resubmitted or skipped while keeping resolver history consistent. | `NimBus.Manager`, `NimBus.WebApp` |
| Messaging patterns | Request/response | Typed request/response over Service Bus sessions with timeout handling. | `NimBus.SDK` |
| Messaging patterns | Message scheduling | Schedule future delivery and cancel scheduled messages (sender mode). | `NimBus.SDK`, `NimBus.ServiceBus` |
| Delivery guarantees | Transactional outbox | Persist outgoing messages in SQL Server outbox and dispatch asynchronously. | `NimBus.Outbox.SqlServer` |
| Extensibility hooks | Pipeline middleware | Middleware behaviors wrap handling for cross-cutting concerns. | `NimBus.Core` |
| Extensibility hooks | Lifecycle observers | Passive hooks for received/completed/failed/dead-lettered message events. | `NimBus.Core` |
| Built-in middleware | Logging/Metrics/Validation middleware | Built-in behaviors for processing logs, OpenTelemetry metrics, and basic message validation. | `NimBus.Core` |
| State and audit | Resolver state tracking | Central resolver stores message outcomes and endpoint resolution state in Cosmos DB. | `NimBus.Resolver`, `NimBus.MessageStore` |
| Operations UI | Management WebApp | Authenticated UI/API for endpoint/event/message inspection and operational workflows. | `NimBus.WebApp` |
| Operations automation | CLI provisioning + operations | `nb` CLI commands for infra/topology/app deploy, purge, session cleanup, and container operations. | `NimBus.CommandLine` |
| Topology management | Declarative topology export/apply | Export platform config and provision topics/subscriptions/rules from it. | `NimBus`, `NimBus.Management.ServiceBus`, `NimBus.CommandLine` |
| Observability | Health checks + resolver lag checks | Health probes for Service Bus/Cosmos and resolver heartbeat age thresholds. | `NimBus.ServiceBus`, `NimBus.MessageStore` |
| Testing | In-memory test transport | Run the full pipeline in tests without Azure Service Bus. | `NimBus.Testing` |
| Local development | Aspire orchestration | Local AppHost orchestrates provisioner, resolver, webapp, publisher, and subscriber. | `NimBus.AppHost`, `NimBus.ServiceDefaults` |

## Extension framework

| Feature | Brief description | Primary packages |
|---|---|---|
| `INimBusExtension` model | Extension packages can register services, middleware, and lifecycle observers through `AddNimBus()`. | `NimBus.Core.Extensions` |
| Fluent builder composition | `INimBusBuilder` supports adding behaviors, observers, and extension instances/types. | `NimBus.Core.Extensions` |
| Backward-compatible opt-in | Without registered extensions, NimBus runs with default core behavior. | `NimBus.Core` |

## Shipped extensions

| Extension | Brief description | Package/project |
|---|---|---|
| Notifications | Sends configurable notifications on lifecycle events (failure/dead-letter by default) through pluggable channels (`INotificationChannel`). | `NimBus.Extensions.Notifications` |
| Identity | Adds ASP.NET Core Identity username/password auth for WebApp, with email confirmation/reset flow and optional dual-login with Entra ID. | `NimBus.Extensions.Identity` |
