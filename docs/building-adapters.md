# Building Adapters

This guide explains how to build a **NimBus adapter**: a process that connects an
external system to the NimBus event bus. An adapter can publish events, subscribe
to events, or do both in the same host.

Use this page when you are building a real integration and need to decide how to
wire hosting, handlers, retries, the outbox, deferred-message replay, and
observability. For the first "hello world" path, start with
[getting-started.md](getting-started.md). For API details, see
[sdk-api-reference.md](sdk-api-reference.md).

## Adapter responsibilities

An adapter usually owns four things:

- **Translation** between external-system models and NimBus event contracts.
- **Publishing** events when its backing system changes.
- **Subscribing** to events published by other endpoints.
- **Reliability boundaries**, including retries, idempotency, and optionally a
  transactional outbox for publish-after-commit scenarios.

Adapters are independent deployables. They use `NimBus.SDK`, Azure Service Bus,
normal .NET dependency injection, and whatever clients they need for the external
system. They do not share in-memory state with the Resolver or WebApp.

Architecture context for Resolver, WebApp, message state, and sessions lives in
[architecture.md](architecture.md). This guide stays focused on adapter code.

## Quick decision guide

### What is the adapter doing?

| Adapter shape | Register |
| --- | --- |
| Publish only | `AddNimBusPublisher("EndpointName")` |
| Subscribe only | `AddNimBus(...)`, `AddNimBusSubscriber(...)`, and a receiver/trigger |
| Publish and subscribe | All of the above |
| Publish after local DB commit | Add `NimBus.Outbox.SqlServer` and the outbox dispatcher |

### How should it be hosted?

| Host | Use when | Sample |
| --- | --- | --- |
| Long-running Worker | Simple local debugging, stateful adapters, in-process outbox dispatcher, explicit control over background services | [`samples/CrmErpDemo/Crm.Adapter/Program.cs`](../samples/CrmErpDemo/Crm.Adapter/Program.cs) |
| Azure Functions isolated worker | Serverless scaling, bursty workloads, session-trigger based consumption, no always-on process | [`samples/CrmErpDemo/Erp.Adapter.Functions/Program.cs`](../samples/CrmErpDemo/Erp.Adapter.Functions/Program.cs) |

Functions-specific setup is covered in
[azure-functions-hosting.md](azure-functions-hosting.md).

### Do you need an outbox?

| Publish path | Use when | Tradeoff |
| --- | --- | --- |
| Direct publish | The adapter does not need to make local DB writes atomic with event publishing | Lower I/O and simpler wiring |
| SQL Server outbox | The adapter writes local state and must only publish if that write commits | Adds table storage and a dispatcher, but closes the commit-then-crash gap |

The outbox is a publisher-side reliability feature. Subscriber handlers still
need to be idempotent because Service Bus delivery, retries, operator resubmit,
and dispatcher recovery are all at-least-once paths.

## Endpoint and contract checklist

Before wiring `Program.cs`, settle these items:

- **Endpoint name**: the NimBus endpoint/topic name, for example
  `CrmEndpoint` or `ErpEndpoint`.
- **Subscription name**: usually the same as the endpoint for the main
  subscriber.
- **Session key**: every event must produce the session ID that should preserve
  ordering. Use `GetSessionId()` or `[SessionKey]`.
- **Event identity**: publish with a stable `MessageId` when the source system
  already has a natural idempotency key.
- **Topology**: create topics, subscriptions, rules, and deferred subscriptions
  before the adapter runs. The SDK does not auto-provision Service Bus topology.

Example event:

```csharp
using NimBus.Core.Events;

public sealed class AccountCreated : Event
{
    public required string AccountId { get; init; }
    public required string Name { get; init; }

    public override string GetSessionId() => AccountId;
}
```

## Common bootstrap

Most adapters start by creating a host and registering an Azure
`ServiceBusClient`.

Aspire-managed Worker:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");
```

Manual registration, common in Functions and non-Aspire hosts:

```csharp
builder.Services.AddSingleton<ServiceBusClient>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()["AzureWebJobsServiceBus"]
        ?? throw new InvalidOperationException("AzureWebJobsServiceBus is required.");

    return new ServiceBusClient(connectionString);
});
```

`ServiceBusClient` must be in DI before `AddNimBusPublisher`,
`AddNimBusSubscriber`, or `AddNimBusReceiver` are used.

## Publisher setup

Register the publisher for the endpoint whose topic receives messages:

```csharp
builder.Services.AddNimBusPublisher("CrmEndpoint");
```

This registers:

- `IPublisherClient`, the usual service to inject into application code.
- `ISender`, the lower-level NimBus sender abstraction.
- NimBus OpenTelemetry runtime components for publish instrumentation.

Publishing from application code:

```csharp
public sealed class CrmEventPublisher(IPublisherClient publisher)
{
    public Task AccountCreatedAsync(Account account)
    {
        var evt = new AccountCreated
        {
            AccountId = account.Id,
            Name = account.Name,
        };

        return publisher.Publish(evt);
    }
}
```

If the external system gives you a stable idempotency key, pass it as the
message ID:

```csharp
await publisher.Publish(
    evt,
    sessionId: evt.AccountId,
    correlationId: command.CorrelationId,
    messageId: $"crm-account-created-{account.Id}");
```

`IPublisherClient` supports:

| Method | Use |
| --- | --- |
| `Publish(IEvent)` | Publish one event using the event's session ID and a new correlation ID |
| `Publish(event, sessionId, correlationId)` | Override the session and correlation IDs |
| `Publish(event, sessionId, correlationId, messageId)` | Also provide an explicit idempotency-oriented `MessageId` |
| `PublishBatch(IEnumerable<IEvent>, correlationId)` | Send multiple events in one Service Bus batch, subject to transport size limits |
| `GetBatches(List<IEvent>)` | Split a list into transport-sized batches before publishing |
| `Request<TRequest,TResponse>(...)` | Request/response over Service Bus sessions; requires a `PublisherClient` created with a `ServiceBusClient` |

`PublisherClient` also has concrete `Schedule(...)` and `CancelScheduled(...)`
methods. Those methods are not on `IPublisherClient`, so inject or create the
concrete client only when scheduled publish is part of the adapter contract.

## Subscriber setup

A subscriber needs three pieces:

- Handler registrations via `AddNimBusSubscriber(...)`.
- Pipeline/lifecycle configuration via `AddNimBus(...)`.
- A receive loop: `AddNimBusReceiver(...)` in a Worker, or a
  `[ServiceBusTrigger]` function in Azure Functions.

### Register the NimBus pipeline

`AddNimBus(...)` is optional. Call it when you want to register pipeline
behaviors or lifecycle observers; skip it entirely for a no-middleware
subscriber:

```csharp
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});
```

Adapters do not register a NimBus message-store provider. Storage providers
(`AddCosmosDbMessageStore`, `AddSqlServerMessageStore`) are for platform hosts
such as the Resolver and WebApp; they are not part of normal adapter wiring.

### Write handlers

Handlers are regular DI-created classes:

```csharp
public sealed class AccountCreatedHandler(ICrmApiClient crm, ILogger<AccountCreatedHandler> log)
    : IEventHandler<AccountCreated>
{
    public async Task Handle(AccountCreated message, IEventHandlerContext context, CancellationToken ct)
    {
        log.LogInformation(
            "Handling {EventType} {EventId} for account {AccountId}",
            context.EventType,
            context.EventId,
            message.AccountId);

        await crm.CreateAccountAsync(message.AccountId, message.Name, ct);
    }
}
```

`IEventHandlerContext` exposes `MessageId`, `EventId`, `EventType`, and
`CorrelationId`. It also exposes `MarkPendingHandoff(...)` for integrations that
start long-running external work and need the message recorded as pending rather
than failed. See [error-handling.md](error-handling.md) and the pending handoff
[ADR](adr/012-pending-handoff.md) for the operational implications.

### Register handlers

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();
    sub.AddHandler<AccountUpdated, AccountUpdatedHandler>();
    sub.AddHandler<AccountDeleted, AccountDeletedHandler>();
});
```

`AddNimBusSubscriber` registers:

- `ISubscriberClient`, which dispatches Service Bus messages through NimBus.
- The handler provider and handler registrations.
- The retry-policy provider when configured.
- A default `IDeferredMessageProcessor` for the endpoint's deferred replay path.

## Worker receiver

For a long-running Worker, add the hosted receiver:

```csharp
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
    opts.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5);
});
```

| Option | Default | Notes |
| --- | --- | --- |
| `TopicName` | Required | Service Bus topic to receive from |
| `SubscriptionName` | Required | Subscription within the topic |
| `MaxConcurrentSessions` | `1` | Number of sessions processed concurrently. Increase this for busy endpoints. |
| `MaxAutoLockRenewalDuration` | `5 min` | Maximum lock renewal duration for long-running handlers |

Per-session ordering is preserved. `MaxConcurrentSessions` only increases
parallelism across different sessions.

## Azure Functions receiver

In Azure Functions, do not call `AddNimBusReceiver`. Register
`AddNimBusSubscriber(...)` in `Program.cs`, then add a Service Bus trigger that
delegates to `ISubscriberClient`:

```csharp
public sealed class CrmEndpointFunction(ISubscriberClient subscriber)
{
    [Function("CrmEndpoint")]
    public Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "%SubscriptionName%",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ServiceBusSessionMessageActions sessionActions,
        CancellationToken ct) =>
        subscriber.Handle(message, messageActions, sessionActions, ct);
}
```

Both pieces are required:

- `AddNimBusSubscriber(...)` configures handlers and the dispatch pipeline.
- `[ServiceBusTrigger]` feeds Service Bus messages into that pipeline.

Missing either one leaves the adapter configured but not consuming messages. See
[azure-functions-hosting.md](azure-functions-hosting.md) for `host.json`,
session settings, local settings, and the deferred processor function.

## Retry and permanent failures

Retry policies are configured per subscriber:

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();

    sub.ConfigureRetryPolicies(policies =>
    {
        policies.AddEventTypePolicy("AccountCreated", new RetryPolicy
        {
            MaxRetries = 5,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(10),
        });
    });
});
```

Use a permanent failure classifier for exceptions that should bypass retries and
go directly to dead-letter handling:

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();

    sub.ConfigurePermanentFailureClassifier(classifier =>
    {
        classifier.AddPermanentExceptionType<InvalidPayloadException>();
        classifier.AddPermanentExceptionNamePattern("Validation");
    });
});
```

The full exception routing model is documented in
[error-handling.md](error-handling.md).

## Deferred messages

NimBus uses Service Bus sessions to preserve ordering. When a session is blocked
by a failed or pending message, later messages in that session are parked on the
deferred subscription and replayed after the blocking message is resolved.

`AddNimBusSubscriber(...)` registers the default `IDeferredMessageProcessor`, but
the adapter still needs something to drive replay:

- In a Worker, add a hosted service like
  [`Crm.Adapter/DeferredProcessorService.cs`](../samples/CrmErpDemo/Crm.Adapter/DeferredProcessorService.cs).
- In Azure Functions, add a second non-session `[ServiceBusTrigger]` like
  [`ErpDeferredProcessorFunction.cs`](../samples/CrmErpDemo/Erp.Adapter.Functions/Functions/ErpDeferredProcessorFunction.cs).

The mechanics are covered in [deferred-messages.md](deferred-messages.md).

## Transactional outbox

Use the SQL Server outbox when the adapter must publish only after a local SQL
transaction commits:

```csharp
builder.Services.AddNimBusSqlServerOutbox(crmConnectionString);

builder.Services.AddSingleton<OutboxDispatcherSender>(sp =>
{
    var client = sp.GetRequiredService<ServiceBusClient>();
    return new OutboxDispatcherSender(client.CreateSender("CrmEndpoint"));
});

builder.Services.AddNimBusOutboxDispatcher(TimeSpan.FromSeconds(1));
builder.Services.AddNimBusPublisher("CrmEndpoint");
```

When `IOutbox` is registered, `AddNimBusPublisher` writes outgoing messages to
the outbox instead of sending directly. The dispatcher reads committed outbox
rows and sends them to Service Bus.

Create the outbox table on startup for fresh databases:

```csharp
var outbox = (SqlServerOutbox)host.Services.GetRequiredService<IOutbox>();
await outbox.EnsureTableExistsAsync();
```

Two important notes:

- Register `OutboxDispatcherSender`; the dispatcher needs a real Service Bus
  sender, not the outbox-decorated `ISender`.
- Outbox dispatch is at-least-once. Consumers must tolerate duplicates.

The complete sample is
[`samples/CrmErpDemo/Crm.Adapter/Program.cs`](../samples/CrmErpDemo/Crm.Adapter/Program.cs).

## Middleware and observers

NimBus dispatches received messages through `IMessagePipelineBehavior`
implementations before invoking the handler.

Built-in behaviors:

| Behavior | Purpose |
| --- | --- |
| `LoggingMiddleware` | Logs start, completion, failure, elapsed time, and message metadata |
| `ValidationMiddleware` | Dead-letters invalid messages, such as messages missing required event metadata |

Register middleware in the order you want it to wrap the handler:

```csharp
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});
```

Behaviors execute in registration order, outermost first. With the registration
above, the receive path is:

```text
Logging -> Validation -> Handler -> Validation -> Logging
```

For passive observation, use `IMessageLifecycleObserver` instead of middleware.
Observers can emit metrics, alerts, or audit records without changing dispatch
flow. Register observers with `n.AddLifecycleObserver<T>()`.

For custom middleware examples, short-circuiting, and observer details, see
[pipeline-middleware.md](pipeline-middleware.md).

## Observability

`AddNimBusPublisher(...)` registers NimBus instrumentation runtime components
for publisher-side decorators. Subscriber-only hosts can register the same
runtime package explicitly:

```csharp
builder.Services.AddNimBusInstrumentation();
```

To export spans and metrics, the host's OpenTelemetry setup must also add the
NimBus meters and activity sources:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddNimBusInstrumentation())
    .WithTracing(tracing => tracing.AddNimBusInstrumentation());
```

NimBus emits publisher, consumer, outbox, deferred processor, resolver, and store
instrumentation through the `NimBus.OpenTelemetry` package. Consumer spans and
metrics are emitted by the transport adapter; they are not middleware behaviors.

## Complete Worker shape

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddHttpClient<ICrmApiClient, CrmApiClient>();

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
});

builder.Services.AddNimBusPublisher("CrmEndpoint");

var host = builder.Build();
host.Run();
```

Add the outbox and deferred replay hosted service when the adapter needs them.

## Topology provisioning

Provision Service Bus topology once per environment before messages flow:

- Use the CLI: `nb topology apply` (see [cli.md](cli.md)).
- Use a provisioner program, such as
  [`samples/AspirePubSub/AspirePubSub.Provisioner/Program.cs`](../samples/AspirePubSub/AspirePubSub.Provisioner/Program.cs).

Topology comes from the `IPlatform` definition: endpoint names, subscriptions,
session settings, retry counts, and routing rules.

## Production checklist

- Set `MaxConcurrentSessions` deliberately. The Worker default is `1`, which
  serializes the whole endpoint.
- Keep handlers idempotent. NimBus gives ordered, at-least-once processing, not
  exactly-once side effects.
- Use a stable `MessageId` when the source event has a natural unique key.
- Prefer the outbox when publishing is coupled to a local database transaction.
- Run the deferred-message replay path for every subscriber endpoint.
- Classify poison payloads as permanent failures so they do not burn retry
  budget.
- Export NimBus OpenTelemetry meters and sources from every host.
- Provision topology before deployment and keep endpoint names consistent across
  code, config, and platform definitions.

## Common pitfalls

- **Messages stay pending**: the handler is not registered, the receiver/trigger
  is missing, or the trigger's topic/subscription settings point to the wrong
  entity.
- **A Worker processes one message at a time**: `MaxConcurrentSessions` was left
  at the default `1`.
- **Functions app starts but nothing is consumed**: `AddNimBusSubscriber(...)`
  exists, but there is no `[ServiceBusTrigger]` function, or
  `IsSessionsEnabled = true` is missing on the main subscription trigger.
- **Outbox rows are written but never published**: `OutboxDispatcherSender` or
  `AddNimBusOutboxDispatcher(...)` is missing.
- **Outbox dispatcher fails on first startup**: the outbox table has not been
  created with `EnsureTableExistsAsync()`.
- **Every message is dead-lettered by validation**: required event metadata such
  as `EventId` or `EventTypeId` is missing. Fix the publisher side.

## Where to go next

- [getting-started.md](getting-started.md) - first publisher/subscriber path.
- [sdk-api-reference.md](sdk-api-reference.md) - publisher, subscriber, retry,
  outbox, and request/response APIs.
- [azure-functions-hosting.md](azure-functions-hosting.md) - isolated worker
  setup, `host.json`, triggers, and deferred processor function.
- [pipeline-middleware.md](pipeline-middleware.md) - custom middleware and
  lifecycle observers.
- [error-handling.md](error-handling.md) - retry, dead-letter, and failure
  classification behavior.
- [deferred-messages.md](deferred-messages.md) - session blocking and deferred
  replay.
- [testing.md](testing.md) - adapter and instrumentation test strategy.
