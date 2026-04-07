# ADR-002: Centralized Resolver for Message State Tracking

## Status
Accepted

## Context
In an event-driven system, operators need visibility into message state: which messages are pending, completed, failed, deferred, or dead-lettered. Two approaches were considered:

1. **Centralized Resolver** — A dedicated service that receives all response messages and maintains state projections in Cosmos DB
2. **Distributed state** — Each endpoint tracks its own state locally, aggregated by the WebApp on demand

## Decision
Use a centralized Resolver implemented as an Azure Function that subscribes to all response messages (ResolutionResponse, ErrorResponse, DeferralResponse, SkipResponse, UnsupportedResponse) via a forwarding subscription on each endpoint topic.

The Resolver:
- Stores every message in an immutable history (Cosmos DB `messages` container)
- Maintains per-endpoint state projections (Cosmos DB per-endpoint containers)
- Maps message types to resolution statuses (EventRequest→Pending, ResolutionResponse→Completed, ErrorResponse→Failed, etc.)
- Handles Cosmos DB throttling with exponential backoff

## Consequences

### Positive
- Single source of truth for all message state across the platform
- Complete audit trail — every message in the chain is preserved
- WebApp queries a single data store instead of polling each endpoint
- Enables platform-wide metrics, insights, and search
- Operators can resubmit/skip from the WebApp without accessing individual endpoints

### Negative
- Single point of failure — if the Resolver is down, state updates are delayed (messages are still processed, just not tracked)
- Additional Cosmos DB cost for storing every message
- Resolver lag — state updates are eventually consistent (not real-time)
- Cosmos DB throttling under high load requires backoff and retry logic
