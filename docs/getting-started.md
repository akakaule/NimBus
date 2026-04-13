# Getting Started with NimBus

This guide walks you through creating your first publisher and subscriber, running them with Aspire, and observing the message flow in the management WebApp.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An Azure Service Bus namespace (Standard or Premium)
- An Azure Cosmos DB account
- Docker Desktop (for Aspire dashboard)

## 1. Define an Event

Events are simple C# classes that extend `Event`. Use `[Description]` and `[Required]` attributes for documentation and schema generation.

```csharp
using NimBus.Core.Events;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

[Description("Published when a customer places a new order.")]
public class OrderPlaced : Event
{
    [Required]
    [Description("The unique order identifier.")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("The customer that placed the order.")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("The ISO currency code for the order total.")]
    public string CurrencyCode { get; set; }

    [Range(0.01, 1000000)]
    [Description("The total amount of the order.")]
    public decimal TotalAmount { get; set; }

    public override string GetSessionId() => OrderId.ToString();
}
```

**Tip:** You can use the `[SessionKey]` attribute instead of overriding `GetSessionId()`:

```csharp
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    public Guid OrderId { get; set; }
    // ... no GetSessionId() override needed
}
```

`GetSessionId()` (or `[SessionKey]`) provides the default session ID for message ordering — all messages with the same session ID are processed in FIFO order. The publisher can also override the session ID explicitly at publish time for advanced ordering scenarios (see [SDK API Reference](sdk-api-reference.md#sessionkey-attribute)).

## 2. Create a Publisher

A publisher is a web app or worker service that sends events to Azure Service Bus.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Register the publisher for the StorefrontEndpoint topic
builder.Services.AddNimBusPublisher("StorefrontEndpoint");

var app = builder.Build();

app.MapPost("/publish/order", async (IPublisherClient publisher) =>
{
    var order = new OrderPlaced
    {
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        CurrencyCode = "EUR",
        TotalAmount = 42.50m,
    };

    await publisher.Publish(order);

    return Results.Ok(new { order.OrderId, Status = "Published" });
});

app.Run();
```

`AddNimBusPublisher("StorefrontEndpoint")` registers `IPublisherClient` in DI. The endpoint name determines which Service Bus topic receives the messages.

## 3. Create a Subscriber

A subscriber is a worker service that handles events.

### Define a handler

```csharp
using NimBus.SDK.EventHandlers;

public class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly ILogger<OrderPlacedHandler> _logger;

    public OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken ct)
    {
        _logger.LogInformation("Order {OrderId} received, amount {Amount} {Currency}",
            message.OrderId, message.TotalAmount, message.CurrencyCode);
        return Task.CompletedTask;
    }
}
```

Handlers use standard DI — inject any service via the constructor. No NimBus-specific logger needed.

### Wire up the subscriber

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Register middleware pipeline
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<MetricsMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

// Register the subscriber and handler
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
});

// Start listening on the Service Bus subscription
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "BillingEndpoint";
    opts.SubscriptionName = "BillingEndpoint";
});

var host = builder.Build();
host.Run();
```

## 4. Add Middleware (Optional)

NimBus includes three built-in middleware behaviors. Register them via `AddNimBus()` before `AddNimBusSubscriber()`:

```csharp
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();     // Logs timing + metadata
    nimbus.AddPipelineBehavior<MetricsMiddleware>();     // OpenTelemetry counters
    nimbus.AddPipelineBehavior<ValidationMiddleware>();  // Dead-letters invalid messages
});
```

See [Pipeline Middleware](pipeline-middleware.md) for writing custom middleware.

## 5. Configure Retry Policies (Optional)

Add automatic retry with configurable backoff:

```csharp
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.ConfigureRetryPolicies(policies =>
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

## 6. Provision Service Bus Topology

Before running, create the topics, subscriptions, and routing rules:

```bash
# Using the CLI
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev

# Or export the config
nb topology export -o platform-config.json
```

With Aspire, the provisioner runs automatically as part of the AppHost.

## 7. Run with Aspire

### Set connection strings as user secrets

```bash
cd src/NimBus.AppHost
dotnet user-secrets set "ConnectionStrings:servicebus" "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key"
dotnet user-secrets set "ConnectionStrings:cosmos" "AccountEndpoint=https://your-account.documents.azure.com:443/;AccountKey=your-key"
```

### Start the AppHost

```bash
dotnet run --project src/NimBus.AppHost
```

This launches:
- **Aspire Dashboard** — Logs, traces, metrics at `https://localhost:17xxx`
- **provisioner** — Creates Service Bus topology (runs once)
- **resolver** — Tracks message state in Cosmos DB
- **webapp** — Management UI
- **publisher** — Sample HTTP API
- **subscriber** — Sample event handler with middleware

### Publish a test message

Find the publisher URL in the Aspire Dashboard, then:

```bash
curl -X POST https://localhost:<port>/publish/order
```

## 8. View in the WebApp

Open the WebApp URL from the Aspire Dashboard:

1. **Endpoints** page — Shows StorefrontEndpoint, BillingEndpoint, WarehouseEndpoint with message counts
2. Click an endpoint → **Endpoint Details** with failed/deferred/pending events
3. Click an event → **Event Details** with:
   - **Message** tab — Identifiers, status, action buttons (Resubmit, Skip, Delete)
   - **Flow** tab — Visual timeline of the complete message lifecycle
   - **Blocked** tab — Events blocked by this session

## 9. Test Error Handling

Publish a message that will fail:

```bash
curl -X POST https://localhost:<port>/publish/order-failed
```

This sends an `OrderPlaced` with `SimulateFailure = true`. The subscriber throws an exception, which:
1. Sends an `ErrorResponse` to the Resolver (status → Failed)
2. Blocks the session (subsequent messages are deferred)
3. Appears in the WebApp with the error text and stack trace

You can then **Resubmit** or **Skip** the failed message from the Event Details page.

## Next Steps

- [Azure Functions Hosting](azure-functions-hosting.md) — Production hosting with Service Bus session triggers
- [Message Flows](message-flows.md) — All 10 message flow patterns
- [Deferred Messages](deferred-messages.md) — Session blocking and deferral mechanics
- [Pipeline Middleware](pipeline-middleware.md) — Custom middleware and lifecycle observers
- [CLI Reference](cli.md) — All `nb` commands
- [Architecture](architecture.md) — System design and component overview
- [SDK API Reference](sdk-api-reference.md) — Complete interface reference
