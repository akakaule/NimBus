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
  - `ScheduleMessage` / `CancelScheduledMessage` — timeouts and deadlines
  - Session ordering — FIFO per entity (e.g., per OrderId)
  - Configurable retry policies — transient failure handling
  - Transactional outbox — reliable publish after state change
  - WebApp message flow timeline — visibility into the full conversation

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

### Example Flow

```
OrderOrchestrator subscribes to:
  - OrderPlaced → save state, publish ReserveInventory, schedule 24h timeout
  - InventoryReserved → update state, publish ChargePayment
  - PaymentCaptured → update state, publish ShipOrder, cancel timeout
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
  "orderId": "...",
  "inventoryReservationId": "...",
  "timeoutSequenceNumber": 12345,
  "createdAt": "2026-04-12T10:00:00Z"
}
```

### Timeout Integration

```csharp
// Schedule a timeout
var seq = await publisher.Schedule(
    new OrderTimeout { OrderId = orderId },
    DateTimeOffset.UtcNow.AddHours(24));

// Save sequence number in state for cancellation
state.TimeoutSequenceNumber = seq;

// Cancel timeout when payment arrives
await publisher.CancelScheduled(state.TimeoutSequenceNumber);
```

## Consequences

### Positive
- NimBus stays focused on messaging infrastructure
- No framework DSL to learn — orchestration is plain C#
- Workflow logic is testable with standard unit tests
- Each orchestrator service is independently deployable and scalable
- State persistence is the application's choice (Cosmos, SQL, etc.)
- NimBus's WebApp message flow timeline shows the full orchestration conversation

### Trade-offs
- No built-in saga visualization in the WebApp (the message flow timeline provides partial visibility)
- Each team writes their own orchestration pattern (mitigated by documentation and sample)
- No framework-enforced compensation (developers must handle it explicitly)

### What NimBus Provides
- Orchestration pattern guide (`docs/orchestration.md` — planned)
- Sample orchestrator service (`samples/AspirePubSub/` — can be extended)
- All messaging primitives: publish, subscribe, schedule, cancel, retry, outbox, session ordering
