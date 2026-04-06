# Message Handling Flows

Complete reference for how messages flow through the NimBus platform's Service Bus topology.

## System Overview

```
                    ┌──────────────┐
  External System ──│ Publisher    │──> Publisher Topic ──┐
                    │ (Azure Func) │                     │
                    └──────────────┘                     │
                                                         │  Event-type forward subscriptions
                    ┌──────────────┐                     │  (SET From, EventId, To)
  External System <─│ Subscriber   │<── Endpoint Topic <─┘
                    │ (Azure Func) │         │
                    └──────────────┘         │
                                             ├── Main subscription (sessions=ON)
                                             ├── Resolver subscription (forward → Resolver topic)
                                             ├── Continuation subscription (forward → self)
                                             ├── Retry subscription (forward → self)
                                             ├── Deferred subscription (sessions=ON)
                                             ├── DeferredProcessor subscription (sessions=OFF)
                                             └── Event-type forward subscriptions
                    ┌──────────────┐
                    │ Resolver     │<── Resolver Topic
                    │ (Azure Func) │
                    └──────────────┘

                    ┌──────────────┐
                    │ WebApp       │──> Manager Topic ──> Forward subscriptions → Endpoint Topics
                    │ (Manager)    │
                    └──────────────┘
```

## Topics

| Topic | Purpose | Created by |
|---|---|---|
| `{endpointId}` | One per endpoint (e.g., `storefrontendpoint`, `billingendpoint`). Carries all messages for that endpoint. | Topology provisioner, per endpoint |
| `Resolver` | Central resolution tracking. Receives all response messages. | Topology provisioner, one per platform |
| `Manager` | Management operations. WebApp sends resubmit/skip here. | Topology provisioner, one per platform |

## Subscriptions per Endpoint Topic

Each endpoint topic has these subscriptions:

| Subscription | Sessions | Forward-To | Filter | Action | Consumer |
|---|---|---|---|---|---|
| `{endpointId}` | ON | — | `user.To = '{endpointId}'` | — | **SubscriberClient** (main handler) |
| `Resolver` | OFF | Resolver topic | `user.To = 'Resolver'` | `SET user.From = '{endpointId}'` | (forwarded) |
| `Resolver` (2nd rule) | OFF | Resolver topic | `user.To = '{endpointId}'` | — | (forwarded, for EventRequest tracking) |
| `Continuation` | OFF | self topic | `user.To = 'Continuation'` | `SET user.To = '{endpointId}'; SET user.From = 'Continuation'` | (forwarded back to main sub) |
| `Retry` | OFF | self topic | `user.To = 'Retry'` | `SET user.To = '{endpointId}'; SET user.From = 'Retry'` | (forwarded back to main sub) |
| `Deferred` | ON | — | `user.To = 'Deferred'` | — | **DeferredMessageProcessor** (reads via AcceptSession) |
| `DeferredProcessor` | OFF | — | `user.To = 'DeferredProcessor'` | — | **DeferredProcessorFunction** |
| `{eventTypeId}` | OFF | consuming endpoint topic | `user.EventTypeId = '{eventTypeId}'` | `SET user.From = '{endpointId}'; SET user.EventId = newid(); SET user.To = '{consumerId}'` | (forwarded to consumer) |

### Key Design Points

- **Session-enabled subscriptions** (`{endpointId}`, `Deferred`): guarantee ordered, exclusive delivery per session
- **Forward subscriptions** (`Continuation`, `Retry`): catch messages, rewrite properties, forward back to the same topic — the rewritten `To` then matches the main subscription
- **DeferredProcessor**: NOT session-enabled — it can trigger independently of the main function's session lock
- **Resolver subscription has two rules**: catches both responses (`To = 'Resolver'`) and original EventRequests (`To = '{endpointId}'`) so the Resolver sees the full message history

---

## Message Flows

### 1. Happy Path: Publish → Handle → Complete

```
Publisher                    Endpoint Topic                     Resolver Topic
   │                             │                                  │
   │── EventRequest ────────────>│                                  │
   │   To={endpointId}           │                                  │
   │   EventTypeId=X             ├─ main sub ─> SubscriberClient    │
   │                             │               │                  │
   │                             │               │ handle (success)  │
   │                             │               │                  │
   │                             │<── ResolutionResponse ──────────>│
   │                             │    To=Resolver                   │
   │                             │    (Resolver sub forwards)       ├─> ResolverService
   │                             │                                  │   status = Completed
```

### 2. Handler Failure → Session Blocked

```
Publisher                    Endpoint Topic                     Resolver Topic
   │                             │                                  │
   │── EventRequest ────────────>│                                  │
   │                             ├─ main sub ─> SubscriberClient    │
   │                             │               │                  │
   │                             │               │ handle (THROWS)   │
   │                             │               │ BlockSession()    │
   │                             │               │                  │
   │                             │<── ErrorResponse ───────────────>│
   │                             │    To=Resolver                   ├─> ResolverService
   │                             │                                  │   status = Failed
   │                             │                                  │
   │                             │   Session state:                 │
   │                             │   BlockedByEventId = event1      │
```

### 3. Deferral: Subsequent Messages on Blocked Session

```
Publisher                    Endpoint Topic                          Resolver Topic
   │                             │                                       │
   │── EventRequest (msg2) ─────>│                                       │
   │   (same session as msg1)    ├─ main sub ─> SubscriberClient         │
   │                             │               │                       │
   │                             │               │ SessionBlockedException│
   │                             │               │                       │
   │                             │<── DeferralResponse ─────────────────>│
   │                             │    To=Resolver                        ├─> status = Deferred
   │                             │                                       │
   │                             │<── Deferred message                   │
   │                             │    To=Deferred                        │
   │                             │    DeferralSequence=0                 │
   │                             ├─ Deferred sub (parked)                │
   │                             │                                       │
   │                             │   Session state:                      │
   │                             │   BlockedByEventId = event1           │
   │                             │   DeferredCount = 1                   │
```

### 4. Resubmission → Unblock → Reprocess Deferred

```
WebApp          Manager Topic        Endpoint Topic                    Resolver
  │                  │                     │                              │
  │── Resubmit ─────>│                     │                              │
  │   To=endpoint     ├─ fwd sub ─────────>│                              │
  │   From=Manager    │                    ├─ main sub ─> SubscriberClient│
  │                   │                    │               │               │
  │                   │                    │               │ handle (ok)    │
  │                   │                    │               │ UnblockSession │
  │                   │                    │               │               │
  │                   │                    │<── ResolutionResponse ───────>│ status=Completed
  │                   │                    │    To=Resolver                │
  │                   │                    │                               │
  │                   │                    │<── ProcessDeferredRequest     │
  │                   │                    │    To=DeferredProcessor       │
  │                   │                    │                               │
  │                   │                    ├─ DeferredProcessor sub        │
  │                   │                    │   (sessions=OFF)              │
  │                   │                    │                               │
  │                   │                    │   DeferredProcessorFunction   │
  │                   │                    │    │                          │
  │                   │                    │    │ AcceptSession on         │
  │                   │                    │    │ Deferred sub             │
  │                   │                    │    │ Read msg2, msg3          │
  │                   │                    │    │ Sort by DeferralSequence │
  │                   │                    │    │                          │
  │                   │                    │<── Republish msg2             │
  │                   │                    │<── Republish msg3             │
  │                   │                    │    To={endpointId}            │
  │                   │                    │                               │
  │                   │                    ├─ main sub ─> handle msg2 ───>│ status=Completed
  │                   │                    ├─ main sub ─> handle msg3 ───>│ status=Completed
```

### 5. Skip → Unblock → Reprocess Deferred

Same as resubmission but the failed event is marked as Skipped instead of re-processed:

```
WebApp          Manager Topic        Endpoint Topic                    Resolver
  │                  │                     │                              │
  │── Skip ─────────>│                     │                              │
  │   To=endpoint     ├─ fwd sub ─────────>│                              │
  │   From=Manager    │                    ├─ main sub ─> SubscriberClient│
  │                   │                    │               │               │
  │                   │                    │               │ UnblockSession │
  │                   │                    │               │ (NO re-handle) │
  │                   │                    │               │               │
  │                   │                    │<── SkipResponse ────────────>│ status=Skipped
  │                   │                    │    To=Resolver                │
  │                   │                    │                               │
  │                   │                    │<── ProcessDeferredRequest     │
  │                   │                    │    (same deferred flow as #4) │
```

### 6. Retry (Automatic)

When a handler fails and a retry policy matches the exception:

```
                         Endpoint Topic                               Resolver
                              │                                          │
  EventRequest ──────────────>│                                          │
                              ├─ main sub ─> handle (THROWS)             │
                              │               │ BlockSession             │
                              │               │ CheckForRetry → match!   │
                              │               │                          │
                              │<── ErrorResponse ───────────────────────>│ status=Failed
                              │<── RetryRequest                          │
                              │    To=Retry                              │
                              │    ScheduledEnqueue = +N minutes         │
                              │                                          │
                              ├─ Retry sub (forward → self)              │
                              │   Action: SET To={endpointId}            │
                              │           SET From=Retry                 │
                              │                                          │
              (after delay)   ├─ main sub ─> HandleRetryRequest          │
                              │               │ handle (success)          │
                              │               │ UnblockSession            │
                              │               │                          │
                              │<── ResolutionResponse ──────────────────>│ status=Completed
                              │<── ProcessDeferredRequest (if deferred)  │
```

### 7. Continuation (Legacy Deferral)

For messages deferred using the older session-state approach (sequence numbers stored in `DeferredSequenceNumbers`):

```
                         Endpoint Topic
                              │
  Resubmit succeeds ─────────>│
                              │ UnblockSession
                              │ ReceiveNextDeferred → found in session state
                              │
                              │<── ContinuationRequest
                              │    To=Continuation
                              │
                              ├─ Continuation sub (forward → self)
                              │   Action: SET To={endpointId}
                              │           SET From=Continuation
                              │
                              ├─ main sub ─> HandleContinuationRequest
                              │               │ Authorize (From=Continuation)
                              │               │ ReceiveNextDeferredWithPop
                              │               │ HandleEventRequest(deferred msg)
                              │               │ ContinueWithAnyDeferredMessages
                              │               │   (recursive for remaining)
```

### 8. Heartbeat

Heartbeat messages bypass all handler logic and session checks:

```
                         Endpoint Topic                    Resolver
                              │                               │
  EventRequest ──────────────>│                               │
  EventTypeId=Heartbeat       ├─ main sub ─> SubscriberClient │
                              │               │                │
                              │               │ if "Heartbeat" │
                              │               │   skip handler │
                              │               │   skip session │
                              │               │                │
                              │<── ResolutionResponse ────────>│ status=Completed
```

### 9. Unsupported Event Type

No handler registered for the event type:

```
                         Endpoint Topic                    Resolver
                              │                               │
  EventRequest ──────────────>│                               │
  EventTypeId=Unknown         ├─ main sub ─> SubscriberClient │
                              │               │                │
                              │               │ handler lookup │
                              │               │ NOT FOUND      │
                              │               │ (no session    │
                              │               │  block)        │
                              │               │                │
                              │<── UnsupportedResponse ──────>│ status=Unsupported
```

### 10. Event-Type Forwarding (Cross-Endpoint Routing)

How events flow from a producing endpoint to a consuming endpoint:

```
Storefront Publisher      Storefront Topic                     Billing Topic
     │                        │                                  │
     │── EventRequest ───────>│                                  │
     │   EventTypeId=         │                                  │
     │   OrderPlaced          ├─ storefrontendpoint sub          │
     │                        │   (handles locally)              │
     │                        │                                  │
     │                        ├─ OrderPlaced sub                 │
     │                        │   Filter: EventTypeId=OrderPlaced│
     │                        │   Action: SET From=storefront    │
     │                        │           SET EventId=newid()    │
     │                        │           SET To=billingendpoint │
     │                        │   Forward: billingendpoint       │
     │                        │                                  │
     │                        │──────────────────────────────────>│
     │                        │                                  ├─ billingendpoint sub
     │                        │                                  │   Billing SubscriberClient
     │                        │                                  │   handles independently
```

---

## Retry Policies

When a handler fails, the `StrictMessageHandler` checks for a matching retry policy via `IRetryPolicyProvider`.

### Backoff Strategies

| Strategy | Delay Calculation | Example (BaseDelay=5s) |
|---|---|---|
| **Fixed** | Always `BaseDelay` | 5s, 5s, 5s, 5s |
| **Linear** | `BaseDelay × (attempt + 1)` | 5s, 10s, 15s, 20s |
| **Exponential** | `BaseDelay × 2^attempt` | 5s, 10s, 20s, 40s |

All strategies respect an optional `MaxDelay` cap.

### Policy Resolution Order

1. **Exception-based rules** — match specific exception types/messages to specific policies
2. **Event-type rules** — match by EventTypeId
3. **Default policy** — fallback if no specific rule matches
4. **No policy** — if no retry policy provider is configured, no retries are attempted

### Configuration

Retry policies are configured via `AddNimBusSubscriber(...).ConfigureRetryPolicies(...)`:

```csharp
services.AddNimBusSubscriber("billingendpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>()
       .ConfigureRetryPolicies(policies =>
       {
           policies.AddEventTypePolicy("OrderPlaced", new RetryPolicy
           {
               MaxRetries = 3,
               Strategy = BackoffStrategy.Exponential,
               BaseDelay = TimeSpan.FromSeconds(5),
               MaxDelay = TimeSpan.FromMinutes(5)
           });
       });
});
```

**Source:** `src/NimBus.Core/Messages/RetryPolicy.cs`

---

## Session State

Stored in Service Bus session state (JSON serialized):

```json
{
  "BlockedByEventId": "event-guid-or-null",
  "DeferredSequenceNumbers": [],
  "DeferredCount": 0,
  "NextDeferralSequence": 0
}
```

| Field | Purpose |
|---|---|
| `BlockedByEventId` | Event ID that caused the failure. Prevents other messages in session from processing. |
| `DeferredSequenceNumbers` | Legacy: sequence numbers of messages deferred within session state |
| `DeferredCount` | New: count of messages sent to the Deferred subscription |
| `NextDeferralSequence` | Counter for ordering deferred messages (FIFO) |

---

## Cosmos DB State Tracking

The Resolver stores every message state change in Cosmos DB:

| Resolution Status | Triggered by | Meaning |
|---|---|---|
| `Pending` | EventRequest, ResubmissionRequest, RetryRequest | Message received, awaiting processing |
| `Completed` | ResolutionResponse | Successfully processed |
| `Failed` | ErrorResponse | Handler threw non-transient exception |
| `Deferred` | DeferralResponse | Session blocked, message parked |
| `Skipped` | SkipResponse | Manager chose to skip this event |
| `Unsupported` | UnsupportedResponse | No handler registered for event type |
| `DeadLettered` | Dead-letter queue | Unexpected exception, message unprocessable |

---

## Design Notes

- `ProcessDeferredRequest` messages are handled by `DeferredProcessorFunction` (a separate Azure Function on the `DeferredProcessor` subscription, sessions=OFF), NOT by `StrictMessageHandler`. This was cleaned up — `StrictMessageHandler.HandleProcessDeferredRequest()` has been removed.
- The `DeferredProcessorFunction` cannot reset the endpoint session state's `DeferredCount` because it runs on a non-session subscription. The stale count is harmless — it only causes a no-op `ProcessDeferredRequest` if a subsequent resubmit/skip occurs on an already-unblocked session.
- All message handlers must be **idempotent** — messages may be redelivered by Service Bus on transient failures.
- **Correlation IDs** are preserved across the entire message chain for distributed tracing via OpenTelemetry.
