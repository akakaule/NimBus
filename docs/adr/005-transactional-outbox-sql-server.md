# ADR-005: Transactional Outbox with SQL Server

## Status
Accepted

## Context
Without an outbox, if a process crashes between committing a database change and sending the Service Bus message, the message is lost silently. This is the most critical reliability gap in any messaging system.

Options for the outbox store:
1. **SQL Server** — Transactional with the application database, ACID guarantees
2. **Cosmos DB** — Already used by the Resolver, but cross-partition transactions are limited
3. **In-memory** — Simple but no durability (useless for crash recovery)

## Decision
Implement `IOutbox` abstraction in `NimBus.Core/Outbox/` with a SQL Server implementation in `NimBus.Outbox.SqlServer/`.

Architecture:
- `OutboxSender` decorates `ISender` — intercepts `Send()` calls and writes to the outbox table instead of Service Bus
- `OutboxDispatcher` (hosted service) polls the outbox table, sends to the real `ISender`, and marks messages as dispatched
- `IOutboxCleanup` purges delivered messages after a configurable retention period

The outbox participates in the same SQL transaction as the application's business logic, ensuring atomicity.

## Consequences

### Positive
- Guarantees at-least-once delivery — messages survive process crashes
- Transactional with application data (SQL Server)
- Transparent to publishers — `OutboxSender` decorates `ISender` without changing the publishing API
- Configurable dispatch interval and batch size

### Negative
- Requires SQL Server (additional infrastructure if not already used)
- Adds latency — messages are dispatched on a polling interval, not immediately
- Outbox table grows until cleanup runs
- Not integrated with Cosmos DB (would require a separate implementation for Cosmos-only deployments)
