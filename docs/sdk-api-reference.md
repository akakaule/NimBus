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
| `Publish(event, sessionId, correlationId)` | Publish with explicit session and correlation IDs. |
| `Publish(event, sessionId, correlationId, messageId)` | Publish with all IDs explicit (for deterministic deduplication). |
| `PublishBatch(events)` | Publish multiple events as a Service Bus batch. Respect batch size limits. |

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

All events must override `GetSessionId()` to define the session key for ordered processing.

### Recommended attributes

| Attribute | Purpose |
|---|---|
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
