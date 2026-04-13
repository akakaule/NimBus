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
}
```

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
| `MetricsMiddleware` | Records `nimbus.pipeline.duration`, `nimbus.pipeline.processed`, `nimbus.pipeline.failed` |
| `ValidationMiddleware` | Dead-letters messages with missing EventId or EventTypeId |

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
