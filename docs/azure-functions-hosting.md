# Hosting Subscribers with Azure Functions

This guide explains how to host NimBus subscribers as Azure Functions using the .NET isolated worker model with Service Bus session triggers. This is the recommended production hosting model.

## Why Azure Functions?

- **Session-enabled triggers** — Azure Functions natively supports Service Bus session processors, which NimBus requires for ordered message delivery
- **Auto-scaling** — Scale based on queue depth and session count
- **Pay-per-execution** — No cost when idle (Consumption plan)
- **Managed infrastructure** — No servers to manage
- **Built-in monitoring** — Application Insights integration out of the box

## Project Setup

### 1. Create the Function App project

```bash
dotnet new worker -n MyEndpoint.Functions --framework net10.0
```

Add the required packages:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.0.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.ServiceBus" Version="5.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.OpenTelemetry" Version="1.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="path/to/NimBus.SDK.csproj" />
    <ProjectReference Include="path/to/NimBus.ServiceBus.csproj" />
    <ProjectReference Include="path/to/NimBus.csproj" />
  </ItemGroup>

</Project>
```

### 2. Configure host.json

```json
{
  "version": "2.0",
  "extensionBundle": {
    "id": "Microsoft.Azure.Functions.ExtensionBundle",
    "version": "[4.*, 5.0.0)"
  },
  "extensions": {
    "serviceBus": {
      "prefetchCount": 0,
      "autoCompleteMessages": false,
      "maxAutoLockRenewalDuration": "00:05:00",
      "maxConcurrentSessions": 200,
      "sessionIdleTimeout": "00:00:30",
      "clientRetryOptions": {
        "mode": "Exponential",
        "tryTimeout": "00:01:00",
        "delay": "00:00:00.8",
        "maxDelay": "00:01:00",
        "maxRetries": 3
      }
    }
  }
}
```

Key settings:
- **`autoCompleteMessages: false`** — NimBus manages message settlement (complete/abandon/dead-letter) explicitly
- **`maxConcurrentSessions: 200`** — Controls how many sessions can be processed concurrently
- **`sessionIdleTimeout: 30s`** — How long to wait for new messages in a session before releasing it
- **`maxAutoLockRenewalDuration: 5min`** — Maximum time to renew the message lock during processing

### 3. Configure Program.cs

```csharp
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// OpenTelemetry for distributed tracing
builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

// Register middleware pipeline
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

// Register subscriber with handler(s)
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
});

builder.Build().Run();
```

### 4. Create the Function

```csharp
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.SDK;

public class BillingEndpointFunction
{
    private readonly ISubscriberClient _subscriber;

    public BillingEndpointFunction(ISubscriberClient subscriber)
    {
        _subscriber = subscriber;
    }

    [Function("BillingEndpoint")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "%SubscriptionName%",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ServiceBusSessionMessageActions sessionActions)
    {
        await _subscriber.Handle(message, messageActions, sessionActions);
    }
}
```

Key points:
- **`IsSessionsEnabled = true`** — Required for NimBus's ordered processing
- **`%TopicName%` / `%SubscriptionName%`** — Read from app settings (see below)
- **`ServiceBusMessageActions`** — Used for message settlement (complete, dead-letter)
- **`ServiceBusSessionMessageActions`** — Used for session state management (block, unblock)

### 5. Configure local.settings.json

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsServiceBus": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
    "TopicName": "BillingEndpoint",
    "SubscriptionName": "BillingEndpoint"
  }
}
```

## Adding a DeferredProcessor Function

In NimBus, deferred message processing is handled separately from the main handler. Add a second function in the same project for the DeferredProcessor subscription:

```csharp
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

public class BillingDeferredProcessorFunction
{
    private readonly IDeferredMessageProcessor _processor;
    private readonly ServiceBusClient _sbClient;

    public BillingDeferredProcessorFunction(
        IDeferredMessageProcessor processor,
        ServiceBusClient sbClient)
    {
        _processor = processor;
        _sbClient = sbClient;
    }

    [Function("BillingDeferredProcessor")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "DeferredProcessor",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        var sessionId = message.ApplicationProperties.TryGetValue("SessionId", out var sid)
            ? sid?.ToString()
            : message.SessionId;

        if (string.IsNullOrEmpty(sessionId))
        {
            await messageActions.DeadLetterMessageAsync(message, "No SessionId");
            return;
        }

        try
        {
            await _processor.ProcessDeferredMessagesAsync(sessionId, "BillingEndpoint");
            await messageActions.CompleteMessageAsync(message);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            await messageActions.CompleteMessageAsync(message);
        }
    }
}
```

Register `IDeferredMessageProcessor` in Program.cs:

```csharp
builder.Services.AddSingleton<IDeferredMessageProcessor>(sp =>
{
    var sbClient = sp.GetRequiredService<ServiceBusClient>();
    return new DeferredMessageProcessor(sbClient);
});
```

Key difference from the main function:
- **`IsSessionsEnabled = false`** — The DeferredProcessor subscription is NOT session-enabled
- It receives a trigger message, then processes deferred messages from the Deferred subscription independently

## Multiple Endpoints in One Function App

You can host multiple endpoint handlers in a single Function App project:

```csharp
// Program.cs
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, BillingOrderPlacedHandler>();
});

// Register a second subscriber as a named service if needed,
// or use separate Function App projects for isolation.
```

For production, **separate Function App projects per endpoint** is recommended for:
- Independent scaling
- Independent deployment
- Fault isolation

## Worker Service vs Azure Functions

| Aspect | Azure Functions | Worker Service (Aspire sample) |
|---|---|---|
| **Hosting** | Serverless / App Service Plan | Self-hosted / Container |
| **Scaling** | Auto-scale by session count | Manual / Kubernetes HPA |
| **Session support** | Native via `IsSessionsEnabled` | Via `AddNimBusReceiver()` |
| **Cost** | Pay-per-execution (Consumption) | Always-on |
| **DeferredProcessor** | Separate function (sessions=OFF) | Background `DeferredProcessorService` |
| **Best for** | Production workloads | Local development, Aspire |

Both models use the same `ISubscriberClient` and `IEventHandler<T>` — the handler code is identical regardless of hosting model.

## Deployment

Deploy using the NimBus CLI:

```bash
nb deploy apps --solution-id nimbus --environment prod --resource-group rg-nimbus-prod
```

Or deploy manually:

```bash
dotnet publish -c Release -o ./publish
cd publish
func azure functionapp publish <function-app-name>
```

## Key Source Files

| File | Purpose |
|---|---|
| `src/NimBus.Resolver/Functions.cs` | Reference implementation (Resolver as Azure Function) |
| `src/NimBus.Resolver/Program.cs` | Function App DI configuration |
| `src/NimBus.Resolver/host.json` | Service Bus session configuration |
| `src/NimBus.SDK/ISubscriberClient.cs` | Subscriber interface (Handle methods) |
| `src/NimBus.ServiceBus/DeferredMessageProcessor.cs` | Deferred message batch processing |
