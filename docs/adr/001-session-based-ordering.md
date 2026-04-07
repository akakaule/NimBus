# ADR-001: Use Service Bus Sessions for Ordered Delivery

## Status
Accepted

## Context
NimBus needs guaranteed FIFO ordering per logical entity (e.g., all messages for a single order must be processed sequentially). Without ordering, a payment confirmation could be processed before the order creation, or a retry could execute while the original is still being handled.

Options considered:
1. **Service Bus sessions** — Exclusive lock per session ID, ordered delivery within session
2. **Partitioned queues** — Distribute messages across partitions by key
3. **Application-level locking** — Distributed lock (Redis/Cosmos) before processing
4. **Single consumer** — One consumer per entity type (no concurrency)

## Decision
Use Azure Service Bus sessions with the entity ID (e.g., OrderId) as the session ID. Session IDs are determined in two ways:

1. **Event-defined (default)** — Each event class overrides `GetSessionId()` to provide a default session key (e.g., `OrderId.ToString()`). Used when calling `publisher.Publish(event)`.
2. **Publisher-controlled (explicit)** — The publisher can override the session ID at publish time via `publisher.Publish(event, sessionId, correlationId)` or `publisher.Publish(event, sessionId, correlationId, messageId)`. This enables scenarios where:
   - The ordering key differs from the event's natural key
   - Multiple event types should share a session for cross-event ordering
   - The publisher needs deterministic message IDs for deduplication

When a handler fails, the session is blocked — subsequent messages for that session are deferred to a separate subscription and replayed in FIFO order after recovery.

## Consequences

### Positive
- Guaranteed FIFO ordering per session without application-level locking
- Built-in session state for tracking blocked/deferred status
- Concurrent processing across different sessions (high throughput)
- Native Service Bus feature — no external dependencies

### Negative
- Ties the platform to Azure Service Bus (sessions are protocol-specific, not available in RabbitMQ/Kafka)
- Session lock management adds complexity (lock renewal, idle timeout)
- Maximum concurrent sessions is configurable but bounded
- Testing requires session-aware mocking (addressed by `NimBus.Testing` in-memory transport)
