# ADR-002: Centralized Resolver for Message State Tracking

## Status
Accepted

## Context
In an event-driven system, operators need visibility into message state: which messages are pending, completed, failed, deferred, or dead-lettered. Two approaches were considered:

1. **Centralized Resolver** — A dedicated service that receives all response messages and maintains state projections in Cosmos DB
2. **Distributed state** — Each endpoint tracks its own state locally, aggregated by the WebApp on demand

A key design principle: **subscribers should only depend on Azure Service Bus** — no database connections, no external APIs, no state stores. All session blocking, deferral, and ordering is managed through Service Bus session state. This keeps subscriber apps simple, stateless, and focused solely on business logic.

## Decision
Use a centralized Resolver implemented as an Azure Function that subscribes to all response messages (ResolutionResponse, ErrorResponse, DeferralResponse, SkipResponse, UnsupportedResponse) via a forwarding subscription on each endpoint topic.

The Resolver:
- Stores every message in an immutable history (Cosmos DB `messages` container)
- Maintains per-endpoint state projections (Cosmos DB per-endpoint containers)
- Maps message types to resolution statuses (EventRequest→Pending, ResolutionResponse→Completed, ErrorResponse→Failed, etc.)
- Handles Cosmos DB throttling with exponential backoff

Subscribers:
- Depend only on Azure Service Bus (no Cosmos DB, no external services)
- Use Service Bus session state for blocking, deferral tracking, and ordering
- Send response messages (ResolutionResponse, ErrorResponse, etc.) back to the Service Bus — the Resolver picks them up asynchronously
- Are completely unaware of the Resolver, Cosmos DB, or the WebApp

## Consequences

### Design Choice
- Subscribers are intentionally kept as pure Service Bus consumers. All state tracking, audit trails, and operational tooling are handled by the Resolver, not by the subscribers. This means a subscriber app only needs a Service Bus connection string — nothing else.
- Session state (BlockedByEventId, DeferredCount) lives in Service Bus, not in a database. The transport layer is the only stateful dependency for subscribers.

### Positive
- Single source of truth for all message state across the platform
- Complete audit trail — every message in the chain is preserved
- WebApp queries a single data store instead of polling each endpoint
- Enables platform-wide metrics, insights, and search
- Operators can resubmit/skip from the WebApp without accessing individual endpoints
- **Subscribers have zero infrastructure dependencies beyond Service Bus** — no database connections, credentials, or configuration needed for state tracking

### Negative
- Single point of failure — if the Resolver is down, state updates are delayed (messages are still processed, just not tracked)
- Additional Cosmos DB cost for storing every message
- Resolver lag — state updates are eventually consistent (not real-time)
- Cosmos DB throttling under high load requires backoff and retry logic
