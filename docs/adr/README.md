# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for the NimBus platform. Each ADR captures the context, decision, and consequences of a significant technical choice.

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [001](001-session-based-ordering.md) | Use Service Bus sessions for ordered delivery | Accepted | 2026-01 |
| [002](002-centralized-resolver.md) | Centralized Resolver for message state tracking | Accepted | 2026-01 |
| [003](003-separated-deferred-processor.md) | Separated DeferredProcessor in subscriber apps | Accepted | 2026-03 |
| [004](004-pipeline-behavior-pattern.md) | Delegate-based pipeline behaviors over ASP.NET middleware | Accepted | 2026-03 |
| [005](005-transactional-outbox-sql-server.md) | Transactional outbox with SQL Server | Accepted | 2026-02 |
| [006](006-standard-logging.md) | Microsoft.Extensions.Logging over custom abstraction | Accepted | 2026-04 |
| [007](007-code-first-catalog-export.md) | Code-first EventCatalog and AsyncAPI export | Accepted | 2026-04 |
| [008](008-per-endpoint-cosmos-containers.md) | Per-endpoint Cosmos DB containers | Accepted | 2026-01 |
| [009](009-orchestration-via-application-services.md) | Orchestration via application services, not a saga framework | Accepted | 2026-04 |
| [010](010-pluggable-message-storage.md) | Pluggable message storage providers (SQL Server + Cosmos DB) | Accepted | 2026-05 |
| [011](011-rabbitmq-as-second-transport.md) | RabbitMQ as a second, production-grade transport | Accepted | 2026-05 |
| [012](012-pending-handoff.md) | PendingHandoff outcome for async message completion | Accepted | 2026-05 |
