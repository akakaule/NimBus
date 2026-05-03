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

## Note on the SQL Server provider

The per-endpoint container model does not translate cleanly to SQL Server (it
would mean dynamic DDL at runtime per endpoint). The SQL provider introduced in
ADR-010 uses a single-table-per-concern layout with `EndpointId` as a
discriminator and composite indexes. See `docs/adr/010-pluggable-message-storage.md`
and `docs/storage-providers.md` for details.
