# ADR-009: Orchestration via Application Services, Not a Saga Framework

## Status
Accepted

## Context
NimBus needed a strategy for multi-step workflows (e.g., order fulfillment: reserve inventory, charge payment, ship, notify). The roadmap originally planned a saga framework with a state machine DSL, Cosmos DB persistence, and timeout scheduling — similar to MassTransit Automatonymous or NServiceBus sagas.

Options considered:
1. **Saga framework in NimBus** — State machine DSL, saga persistence, timeout scheduling, compensation actions, WebApp visualization
2. **Orchestration in application services** — Dedicated microservice uses NimBus as its messaging layer, owns its own state and workflow logic
3. **Choreography only** — No central coordinator, each service reacts to events independently

## Decision
Use option 2: orchestration via application services. NimBus provides the messaging primitives; workflow coordination is application code in a dedicated service.

**Rationale:**

- **NimBus is a messaging platform, not an application framework.** Its value is the transport, ordering, observability, and operational UI — not orchestrating business workflows. Building a saga framework would shift NimBus's focus away from its core strengths.

- **NimBus already provides everything an orchestration service needs:**
  - `Publish` / `Subscribe` — send and receive events
  - `ScheduleMessage` — timeout and deadline messages; direct scheduling can
    also cancel by broker sequence number
  - Session ordering — FIFO per entity (e.g., per OrderId)
  - Configurable retry policies — transient failure handling
  - Transactional SQL outbox — atomic with SQL business state when both writes
    explicitly share one connection and transaction; dispatch is at least once
  - WebApp message history — partial operational visibility into message
    lifecycles, not authoritative business-workflow state

- **A saga framework forces a DSL.** Customers must learn Automatonymous-style state machine syntax. An orchestration service is plain C# — handlers, state in a database, publish commands. Every .NET developer already knows how to write this.

- **Framework sagas are tightly coupled to the framework.** Upgrading NimBus versions, debugging saga state transitions, and testing become framework problems. With application-level orchestration, the service is just another NimBus subscriber.

- **The roadmap says "don't chase NServiceBus feature parity."** A saga framework is the largest single feature in NServiceBus (15+ years of development). Building one would consume months of effort that's better spent on messaging-layer features (inbox pattern, circuit breaker, claim-check).

## Recommended Pattern

### Orchestrator Service

A dedicated microservice (e.g., `OrderOrchestrator`) that:
1. Subscribes to initiating events (e.g., `OrderPlaced`)
2. Tracks workflow state in its own database (Cosmos DB or SQL)
3. Publishes commands to downstream services (e.g., `ReserveInventory`, `ChargePayment`)
4. Subscribes to response events (e.g., `InventoryReserved`, `PaymentFailed`)
5. Uses `ScheduleMessage` for timeouts (e.g., "cancel if payment not received in 24h")
6. Publishes compensation events on failure (e.g., `ReleaseInventory`)

The application must choose its consistency boundary explicitly. NimBus
provides state-plus-outbox atomicity only for SQL state that shares the same SQL
transaction with `SqlServerOutbox`. Cosmos workflow state cannot be atomic with
the NimBus SQL outbox; it needs an application-owned Cosmos outbox or a durable
reconciliation path. The [application-level orchestration guide](../orchestration.md)
defines these supported variants.

### Example Flow

```
OrderOrchestrator subscribes to:
  - OrderPlaced → save state, publish ReserveInventory, schedule 24h timeout
  - InventoryReserved → update state, publish ChargePayment
  - PaymentCaptured → update state, publish ShipOrder, make timeout stale
  - PaymentFailed → update state, publish ReleaseInventory (compensate)
  - ShipmentConfirmed → update state to Completed
  - Timeout expired → publish ReleaseInventory, CancelOrder
```

### State Persistence

The orchestrator owns its state — a simple document per workflow instance:

```json
{
  "id": "order-42",
  "status": "AwaitingPayment",
  "version": 3,
  "orderId": "...",
  "inventoryReservationId": "...",
  "processedMessageIds": ["message-1", "message-2"],
  "timeoutId": "order-42:payment-timeout:1",
  "timeoutSequenceNumber": null,
  "createdAt": "2026-04-12T10:00:00Z",
  "updatedAt": "2026-04-12T10:05:00Z"
}
```

### Timeout Integration

Model a timeout as a deterministic, idempotent message. With a direct
`ISender`, persist the broker sequence returned by `ScheduleMessage` and treat
`CancelScheduledMessage` as a best-effort optimization. With the transactional
outbox, schedule the timeout row in the same SQL transaction as workflow state;
the call returns `0` because no broker sequence exists yet, and cancellation is
not supported. In both modes, the timeout handler must reload state and no-op
when the workflow completed or the timeout identity was superseded. See the
[timeout pattern](../orchestration.md#timeouts-are-messages) for the exact
identity and wiring conventions.

## Consequences

### Positive
- NimBus stays focused on messaging infrastructure
- No framework DSL to learn — orchestration is plain C#
- Workflow logic is testable with standard unit tests
- Each orchestrator service is independently deployable and scalable
- State persistence is the application's choice (Cosmos, SQL, etc.)
- NimBus's WebApp message history provides partial operational visibility,
  while application storage remains authoritative for business status

### Trade-offs
- No built-in saga visualization in the WebApp (the message flow timeline provides partial visibility)
- Each team owns its orchestration code (mitigated by the canonical guide and
  planned reference sample)
- No framework-enforced compensation (developers must handle it explicitly)

### What NimBus Provides
- [Application-level orchestration guide](../orchestration.md)
- A planned reference service under `samples/AspirePubSub/OrchestratorService/`
- All messaging primitives: publish, subscribe, schedule, cancel, retry, outbox, session ordering
