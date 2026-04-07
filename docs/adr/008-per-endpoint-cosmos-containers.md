# ADR-008: Per-Endpoint Cosmos DB Containers

## Status
Accepted

## Context
The Resolver needs to store event state projections (pending, failed, deferred, completed events) and query them efficiently per endpoint. Two approaches:

1. **Single container** — All events in one container, partitioned by endpointId
2. **Per-endpoint containers** — One Cosmos DB container per endpoint (e.g., `BillingEndpoint`, `WarehouseEndpoint`)

A separate `messages` container stores the immutable message history (partitioned by eventId).

## Decision
Use per-endpoint containers for event state projections. Each endpoint gets its own container with `/id` as the partition key (composite key of `eventId_sessionId`).

The `messages` container is shared across all endpoints, partitioned by `/eventId`.

An `audits` container stores the audit trail, partitioned by `/eventId`.

## Consequences

### Positive
- Natural isolation — queries for a single endpoint don't scan other endpoints' data
- Container-level throughput control — can provision RU/s per endpoint based on load
- Simple purge — `PurgeMessages(endpointId)` deletes the entire container
- Copy operations — `container copy` can target a single endpoint without filtering

### Negative
- More containers to manage — one per endpoint plus `messages` and `audits`
- Cross-endpoint queries require querying multiple containers
- Container creation on first use (handled by the Resolver)
- Higher baseline cost if many endpoints have low throughput (minimum RU/s per container)
