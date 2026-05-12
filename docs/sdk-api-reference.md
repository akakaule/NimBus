# SDK API Reference

Concise reference for the NimBus SDK public interfaces and registration methods.

## DI Registration

### AddNimBusPublisher

```csharp
services.AddNimBusPublisher("EndpointName");
```

Registers `IPublisherClient` as a singleton. The endpoint name determines the Service Bus topic.

### AddNimBusSubscriber

```csharp
services.AddNimBusSubscriber("EndpointName", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.ConfigureRetryPolicies(policies => { ... });
});
```

Registers `ISubscriberClient` with handler mappings and optional retry policies.

### AddNimBusReceiver

```csharp
services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "EndpointName";
    opts.SubscriptionName = "EndpointName";
});
```

Registers a hosted service that listens to a Service Bus session-enabled subscription using `ServiceBusSessionProcessor`.

### AddNimBus

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddLifecycleObserver<AlertingObserver>();
    nimbus.AddExtension<MyExtension>();
});
```

Registers the middleware pipeline and lifecycle observers. Call before `AddNimBusSubscriber`.

---

## Publishing

### IPublisherClient

```csharp
public interface IPublisherClient
{
    Task Publish(IEvent @event);
    Task Publish(IEvent @event, string sessionId, string correlationId);
    Task Publish(IEvent @event, string sessionId, string correlationId, string messageId);
    Task PublishBatch(IEnumerable<IEvent> events);
}
```

| Method | Description |
|---|---|
| `Publish(event)` | Publish a single event. SessionId from `event.GetSessionId()`, auto-generated CorrelationId and MessageId. |
| `Publish(event, sessionId, correlationId)` | Publish with explicit session and correlation IDs. Overrides `GetSessionId()`. |
| `Publish(event, sessionId, correlationId, messageId)` | Publish with all IDs explicit (for deterministic deduplication). |
| `PublishBatch(events)` | Publish multiple events as a Service Bus batch. Respect batch size limits. |

**Session ID controls ordering.** By default, `Publish(event)` uses the event's `GetSessionId()` method. The explicit overloads let the publisher control ordering independently:

```csharp
// Default: session ID from OrderPlaced.GetSessionId() (= OrderId)
await publisher.Publish(order);

// Explicit: group multiple event types under a shared session for cross-event ordering
await publisher.Publish(order, customerId.ToString(), correlationId);

// Explicit with deterministic message ID for deduplication
await publisher.Publish(order, customerId.ToString(), correlationId, $"order-{order.OrderId}");
```

---

## Subscribing

### IEventHandler\<T\>

```csharp
public interface IEventHandler<T> where T : IEvent
{
    Task Handle(T message, IEventHandlerContext context, CancellationToken cancellationToken = default);
}
```

| Parameter | Type | Description |
|---|---|---|
| `message` | `T` | Deserialized event object |
| `context` | `IEventHandlerContext` | Message metadata (EventId, CorrelationId, EventType) |
| `cancellationToken` | `CancellationToken` | For async cancellation |

Handlers are resolved from DI — use constructor injection for dependencies.

### IEventHandlerContext

```csharp
public interface IEventHandlerContext
{
    string EventId { get; }
    string CorrelationId { get; }
    string EventType { get; }
    string MessageId { get; }

    // Read-only state set by MarkPendingHandoff (default: HandlerOutcome.Default).
    HandlerOutcome Outcome { get; }
    HandoffMetadata HandoffMetadata { get; }

    // Signal that the handler has handed work off to a long-running external system.
    // Idempotent — last call wins. See "Async completion via PendingHandoff" below.
    void MarkPendingHandoff(string reason, string externalJobId = null, TimeSpan? expectedBy = null);
}
```

#### Async completion via PendingHandoff

When a handler triggers an external long-running job (e.g. a D365 F&O DMF
import) and the per-entity outcome only arrives later, calling
`ctx.MarkPendingHandoff(...)` and returning normally tells NimBus to:

1. Send a `PendingHandoffResponse` to the Resolver — the audit row is
   recorded as `ResolutionStatus = Pending` with `PendingSubStatus = "Handoff"`.
2. Block the session so sibling messages on the same session defer (FIFO)
   until the external job settles.
3. Complete the inbound Service Bus message — no peek-lock held for the
   duration of the external work.

The user handler is **not** re-invoked when the external system reports
back. Settlement is driven by `IManagerClient.CompleteHandoff` (success) or
`IManagerClient.FailHandoff` (failure). See [ADR-012](adr/012-pending-handoff.md)
and the [PendingHandoff message flow](message-flows.md#13-pendinghandoff-async-completion).

```csharp
public class CreateOrderHandler : IEventHandler<OrderPlaced>
{
    private readonly IDmfClient _dmf;
    private readonly ICorrelationStore _correlations;

    public CreateOrderHandler(IDmfClient dmf, ICorrelationStore correlations)
    {
        _dmf = dmf;
        _correlations = correlations;
    }

    public async Task Handle(OrderPlaced order, IEventHandlerContext ctx, CancellationToken ct)
    {
        // Trigger the long-running external import.
        var jobId = await _dmf.QueueImportAsync(order, ct);

        // Record (eventId, jobId) so the adapter's status checker can
        // call ManagerClient.CompleteHandoff / FailHandoff later.
        await _correlations.SaveAsync(ctx.EventId, jobId, ct);

        // Tell NimBus this is a healthy in-flight handoff. The handler
        // returns normally — no exception, no failure-path side effects.
        ctx.MarkPendingHandoff(
            reason: "DMF import in flight",
            externalJobId: jobId,
            expectedBy: TimeSpan.FromMinutes(15));
    }
}
```

`MarkPendingHandoff` is idempotent (last call wins). If the handler calls
it AND then throws, the failure path takes precedence — an `ErrorResponse`
is sent and the PendingHandoff metadata is discarded. PendingHandoff is NOT
an exception path; see [`error-handling.md`](error-handling.md).

**See also**: a runnable end-to-end showcase wiring this onto the ERP adapter
in the CRM/ERP demo — [`samples/CrmErpDemo/README.md#showcase-pendinghandoff-async-erp-imports`](../samples/CrmErpDemo/README.md#showcase-pendinghandoff-async-erp-imports).

### ISubscriberClient

```csharp
public interface ISubscriberClient
{
    Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken ct);
    void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> factory) where TEvent : IEvent;
}
```

Typically not used directly — `AddNimBusSubscriber` + `AddNimBusReceiver` handle registration and hosting.

---

## Events

### Event (base class)

```csharp
public abstract class Event : IEvent
{
    public abstract string GetSessionId();
}
```

The base class provides a default `GetSessionId()` that returns a random GUID (no ordering). Use `[SessionKey]` or override `GetSessionId()` for ordered processing.

### SessionKey Attribute

Declare the ordering key declaratively — no method override needed:

```csharp
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
}
```

The base `Event.GetSessionId()` reads the attribute via reflection and returns `OrderId.ToString()`.

**Precedence order:**
1. Publisher override — `Publish(event, sessionId, ...)` always wins
2. Method override — `GetSessionId()` override on the event class
3. Attribute — `[SessionKey(nameof(Prop))]` on the event class
4. Default — `Guid.NewGuid().ToString()` (no ordering)

### Recommended attributes

| Attribute | Purpose |
|---|---|
| `[SessionKey(nameof(Prop))]` | Declares the session ordering key (alternative to overriding `GetSessionId()`) |
| `[Description("...")]` | Event and property descriptions (used in AsyncAPI export) |
| `[Required]` | Marks required fields (used in JSON Schema generation) |
| `[Range(min, max)]` | Numeric validation range (used in JSON Schema) |

---

## Middleware Pipeline

### IMessagePipelineBehavior

```csharp
public interface IMessagePipelineBehavior
{
    Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default);
}
```

| Parameter | Type | Description |
|---|---|---|
| `context` | `IMessageContext` | Full message context with metadata, session state, and control methods |
| `next` | `MessagePipelineDelegate` | Delegate to invoke the next behavior or terminal handler |
| `cancellationToken` | `CancellationToken` | Cancellation token |

Call `await next(context, ct)` to continue the pipeline. Skip it to short-circuit.

### Built-in Middleware

| Middleware | Description |
|---|---|
| `LoggingMiddleware` | Logs message processing with timing, event metadata, and outcome |
| `ValidationMiddleware` | Dead-letters messages with missing EventId or EventTypeId |

Consumer-side metrics (`nimbus.message.received`, `nimbus.message.processed`, `nimbus.message.process.duration`) and the `NimBus.Process` span are emitted by `NimBusConsumerInstrumentation` from the transport adapter, not by a pipeline behavior — every subscriber path gets them regardless of pipeline registration.

### IMessageLifecycleObserver

```csharp
public interface IMessageLifecycleObserver
{
    Task OnMessageReceived(MessageLifecycleContext context, CancellationToken ct = default);
    Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken ct = default);
    Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken ct = default);
    Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception exception = null, CancellationToken ct = default);
}
```

All methods have default no-op implementations. Override only the hooks you need.

---

## Retry Policies

### RetryPolicy

```csharp
public record RetryPolicy
{
    public int MaxRetries { get; init; }
    public BackoffStrategy Strategy { get; init; }
    public TimeSpan BaseDelay { get; init; }
    public TimeSpan? MaxDelay { get; init; }
}
```

### BackoffStrategy

| Strategy | Delay Calculation |
|---|---|
| `Fixed` | Always `BaseDelay` |
| `Linear` | `BaseDelay × (attempt + 1)` |
| `Exponential` | `BaseDelay × 2^attempt` |

All strategies respect `MaxDelay` cap if set.

### Registration

```csharp
sub.ConfigureRetryPolicies(policies =>
{
    // Per event type
    policies.AddEventTypePolicy("OrderPlaced", new RetryPolicy { ... });

    // Per exception message pattern
    policies.AddExceptionPolicy("timeout", new RetryPolicy { ... });

    // Default fallback
    policies.SetDefaultPolicy(new RetryPolicy { ... });
});
```

---

## Outbox

### IOutbox

```csharp
public interface IOutbox
{
    Task StoreAsync(OutboxMessage message, CancellationToken ct = default);
    Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int maxCount, CancellationToken ct = default);
    Task MarkAsDispatchedAsync(IEnumerable<string> messageIds, CancellationToken ct = default);
}
```

SQL Server implementation: `NimBus.Outbox.SqlServer`. Register via:

```csharp
services.AddNimBusSqlServerOutbox(options =>
{
    options.ConnectionString = "...";
    options.TableName = "Outbox";
});
```

---

## IMessageContext

Full context available to middleware and internal handlers:

| Property | Type | Description |
|---|---|---|
| `EventId` | `string` | Unique event identifier |
| `MessageId` | `string` | Unique message identifier |
| `EventTypeId` | `string` | Event type name |
| `MessageType` | `MessageType` | Enum: EventRequest, RetryRequest, etc. |
| `SessionId` | `string` | Session for ordered processing |
| `CorrelationId` | `string` | Correlation tracking |
| `From` | `string` | Sender endpoint |
| `To` | `string` | Receiver endpoint |
| `EnqueuedTimeUtc` | `DateTime` | When message was enqueued |
| `RetryCount` | `int?` | Current retry attempt |
| `MessageContent` | `MessageContent` | Event payload and error content |

Control methods: `Complete()`, `Abandon()`, `DeadLetter()`, `Defer()`, `BlockSession()`, `UnblockSession()`.

---

## Message Scheduling

Schedule messages for future delivery and cancel them if no longer needed.

### ISender

```csharp
Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default);
Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default);
```

### PublisherClient

```csharp
// Schedule an event for 30 minutes from now
var seq = await publisher.Schedule(orderReminder, DateTimeOffset.UtcNow.AddMinutes(30));

// Cancel if no longer needed (e.g., payment received before timeout)
await publisher.CancelScheduled(seq);
```

### Outbox integration

When using the transactional outbox, scheduled messages are persisted with `ScheduledEnqueueTimeUtc` and dispatched via `ScheduleMessage` by the `OutboxDispatcher`. Note: `CancelScheduledMessage` throws `NotSupportedException` in outbox mode because the Service Bus sequence number is only assigned after dispatch.

---

## Request/Response

Send a typed request and await a typed response with timeout handling. Uses Azure Service Bus sessions for reply correlation.

### PublisherClient

```csharp
var status = await publisher.Request<GetOrderStatus, OrderStatusResponse>(
    new GetOrderStatus { OrderId = orderId },
    timeout: TimeSpan.FromSeconds(30));
```

| Parameter | Type | Description |
|---|---|---|
| `request` | `TRequest` | The request event (must extend `Event`) |
| `timeout` | `TimeSpan` | Maximum time to wait for a response |
| `cancellationToken` | `CancellationToken` | Optional cancellation |

Returns `TResponse` or throws `TimeoutException` if no response is received within the timeout.

**Requirements:** The `PublisherClient` must be created with a `ServiceBusClient` (via `CreateAsync` or the `ServiceBusClient` constructor). The ISender-only constructor does not support request/response.

### How it works

1. Requester generates a unique `replySessionId` and sets `ReplyTo` and `ReplyToSessionId` on the outgoing message
2. Requester opens a `ServiceBusSessionReceiver` on a reply subscription filtered by the `replySessionId`
3. Responder handles the request and sends a JSON-serialized response to the `ReplyTo` address with the matching session ID
4. Requester receives the response, deserializes it as `TResponse`, and returns

### IRequestHandler\<TRequest, TResponse\>

```csharp
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IEvent
    where TResponse : class
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken = default);
}
```

### Defining request and response types

```csharp
// Request — a normal NimBus event
[Description("Query the current status of an order.")]
[SessionKey(nameof(OrderId))]
public class GetOrderStatus : Event
{
    [Required]
    public Guid OrderId { get; set; }
}

// Response — a plain C# class (serialized as JSON)
public class OrderStatusResponse
{
    public Guid OrderId { get; set; }
    public string Status { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

### Implementing a request handler

```csharp
public class GetOrderStatusHandler : IRequestHandler<GetOrderStatus, OrderStatusResponse>
{
    private readonly IOrderRepository _repository;

    public GetOrderStatusHandler(IOrderRepository repository)
    {
        _repository = repository;
    }

    public async Task<OrderStatusResponse> Handle(GetOrderStatus request, CancellationToken ct)
    {
        var order = await _repository.GetByIdAsync(request.OrderId, ct);
        return new OrderStatusResponse
        {
            OrderId = order.Id,
            Status = order.Status,
            LastUpdated = order.UpdatedAt
        };
    }
}
```

### Message model properties

| Property | Type | Description |
|---|---|---|
| `ReplyTo` | `string` | Reply destination set by the requester |
| `ReplyToSessionId` | `string` | Session ID for reply correlation |

These are set automatically by `PublisherClient.Request()` and mapped to the corresponding `ServiceBusMessage` properties.

### Error handling

| Scenario | Behavior |
|---|---|
| No response within timeout | Throws `TimeoutException` |
| Caller cancels via `CancellationToken` | Throws `OperationCanceledException` |
| No `ServiceBusClient` available | Throws `InvalidOperationException` |

### Limitations

- Requires a reply subscription on the Service Bus topic (e.g., `{EndpointName}-reply` with session support)
- Not supported via the transactional outbox (request/response is inherently synchronous)
- Each request creates and disposes a `ServiceBusSessionReceiver` — not suited for high-throughput scenarios (use pub/sub for that)

---

## Permanent Failure Classification

Classify exceptions as permanent (unrecoverable) to dead-letter immediately without consuming retry budget.

### IPermanentFailureClassifier

```csharp
public interface IPermanentFailureClassifier
{
    bool IsPermanentFailure(Exception exception);
}
```

### Default permanent types

`DefaultPermanentFailureClassifier` classifies these as permanent:
- `FormatException`, `InvalidCastException`, `ArgumentException` (and subtypes), `NotSupportedException`
- Exception type names containing: `Serialization`, `Deserialization`, `Validation`

### Registration

```csharp
// Option 1: Use defaults (register in DI)
services.AddSingleton<IPermanentFailureClassifier, DefaultPermanentFailureClassifier>();

// Option 2: Configure via subscriber builder
services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.ConfigurePermanentFailureClassifier(classifier =>
    {
        classifier.AddPermanentExceptionType<MyBusinessRuleException>();
        classifier.AddPermanentExceptionNamePattern("Constraint");
    });
});
```

**Behavior:** When a handler throws a classified permanent exception, the message is dead-lettered immediately. No `RetryRequest` is sent, no session is blocked, and the retry budget is not consumed.

---

## Health Checks

Standard `IHealthCheck` implementations for ASP.NET Core.

### Registration

```csharp
services.AddServiceBusHealthCheck(serviceBusClient);        // checks ServiceBusClient.IsClosed
services.AddCosmosDbHealthCheck(cosmosClient);               // calls ReadAccountAsync
services.AddResolverLagCheck(cosmosClient, endpointId);      // heartbeat age thresholds
```

### Thresholds (Resolver Lag)

| Status | Heartbeat Age |
|---|---|
| Healthy | < 5 minutes |
| Degraded | 5–15 minutes |
| Unhealthy | > 15 minutes |

### Endpoints

| Path | Purpose |
|---|---|
| `/health` | All health checks |
| `/alive` | Liveness checks (tagged `"live"`) |
| `/ready` | Readiness checks (tagged `"ready"`) |

---

## In-Memory Testing

`NimBus.Testing` provides an in-memory transport for running the full pipeline without Azure Service Bus.

### InMemoryMessageBus

```csharp
var bus = new InMemoryMessageBus();
var publisher = new PublisherClient(bus);

// Publish events
await publisher.Publish(new OrderPlaced { OrderId = Guid.NewGuid() });

// Deliver to handler
await bus.DeliverAll(messageHandler);

// Assert
Assert.AreEqual(1, bus.SentMessages.Count);
```

### NimBusTestFixture

```csharp
var fixture = new NimBusTestFixture();
fixture.RegisterHandler<OrderPlaced>(() => new OrderPlacedHandler());

await fixture.Publisher.Publish(order);
await fixture.DeliverAll();
```

### DI Registration

```csharp
services.AddNimBusTestTransport();  // replaces Service Bus with in-memory
```

### Scheduled Messages (testing)

```csharp
var bus = new InMemoryMessageBus();
var seq = await bus.ScheduleMessage(message, DateTimeOffset.UtcNow.AddHours(1));

Assert.AreEqual(1, bus.ScheduledMessages.Count);

await bus.CancelScheduledMessage(seq);
Assert.AreEqual(0, bus.ScheduledMessages.Count);
```

---

## Identity Extension

`NimBus.Extensions.Identity` adds username/password authentication with email verification as an alternative to Entra ID. Opt-in — the core WebApp has no SQL Server dependency without it.

### Registration

```csharp
services.AddNimBusIdentity(options =>
{
    options.ConnectionString = "Server=...";
    options.RequireEmailConfirmation = true;
    options.EnableEntraIdLogin = true;  // show "Sign in with Microsoft" alongside local login
    options.Smtp = new SmtpOptions
    {
        Host = "smtp.example.com",
        Port = 587,
        FromAddress = "nimbus@example.com"
    };
});
```

### Auth modes

| Mode | Condition |
|---|---|
| Entra ID only | No Identity registered (default) |
| Identity only | Identity registered, no AzureAd config |
| Dual | Both configured — login page shows both options |

### Claims mapping

Identity users are automatically mapped to the same claims the WebApp's `EndpointAuthorizationService` checks (`oid`, `name`, `groups`). No changes to authorization logic needed.

---

## Key Source Files

| Component | File |
|---|---|
| IPublisherClient | `src/NimBus.SDK/IPublisherClient.cs` |
| ISubscriberClient | `src/NimBus.SDK/ISubscriberClient.cs` |
| IEventHandler\<T\> | `src/NimBus.SDK/EventHandlers/IEventHandler.cs` |
| IEventHandlerContext | `src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs` |
| IMessagePipelineBehavior | `src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs` |
| IMessageLifecycleObserver | `src/NimBus.Core/Extensions/IMessageLifecycleObserver.cs` |
| RetryPolicy | `src/NimBus.Core/Messages/RetryPolicy.cs` |
| IOutbox | `src/NimBus.Core/Outbox/IOutbox.cs` |
| DI extensions | `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs` |
| Built-in middleware | `src/NimBus.Core/Pipeline/` |
