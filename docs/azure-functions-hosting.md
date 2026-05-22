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

In NimBus, deferred message processing is handled separately from the main handler. Functions hosts need a dedicated function on the `deferredprocessor` trigger subscription:

```csharp
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

public class BillingDeferredProcessorFunction(IDeferredMessageProcessor processor)
{
    [Function("BillingDeferredProcessor")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "deferredprocessor",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "BillingEndpoint");

        if (outcome.Action == DeferredMessageDispatchAction.DeadLetter)
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: outcome.DeadLetterReason);
        else
            await messageActions.CompleteMessageAsync(message);
    }
}
```

`IDeferredMessageProcessor` is registered for you by `AddNimBusSubscriber`. The shared body
that extracts `SessionId`, calls the processor, and handles `SessionCannotBeLocked` lives in
`NimBus.SDK.Hosting.DeferredMessageDispatcher.ProcessAsync` — call it from any Functions
worker that owns its own `[ServiceBusTrigger]` (and from anywhere else that wants to drive
deferred replay).

> **Functions hosts must opt out of the BackgroundService**. By default
> `AddNimBusSubscriber` also registers `DeferredMessageProcessorHostedService`, a Worker-side
> `BackgroundService` that listens on the same `deferredprocessor` subscription. In a
> Functions worker that would compete with the function trigger for the same messages. Disable
> it on the subscriber registration:
>
> ```csharp
> builder.Services.AddNimBusSubscriber(
>     opts =>
>     {
>         opts.Endpoint = "BillingEndpoint";
>         opts.DisableDeferredProcessorHostedService = true;
>     },
>     sub => sub.AddHandlersFromAssemblyContaining<MyHandler>());
> ```
>
> If you forget the opt-out you'll see a startup log line on the BackgroundService side
> (`"Deferred-processor hosted service enabled on topic 'BillingEndpoint'..."`) and intermittent
> duplicate-settlement errors on the Functions side. Flip the option and restart.

### Migrating from a hand-rolled DeferredProcessorService

Worker hosts that previously registered a `DeferredProcessorService` (BackgroundService) by
hand should delete that class and its `AddHostedService(...)` line — `AddNimBusSubscriber`
now wires the equivalent automatically. If you keep the hand-rolled service, also set
`DisableDeferredProcessorHostedService = true` so the two don't compete.

### Subscription naming

Two distinct subscriptions are involved — keeping the names straight matters:

- **`Deferred`** (session-enabled, the *parking lot*) — read by `IDeferredMessageProcessor`
  via `AcceptSessionAsync(sessionId)`. Default constant
  `NimBus.Core.Constants.DeferredSubscriptionName`.
- **`deferredprocessor`** (non-session, the *trigger lot*) — read by the BackgroundService or
  by the `[ServiceBusTrigger]` function class above. Default option
  `NimBusSubscriberOptions.DeferredProcessorSubscriptionName`.

Key difference from the main function:
- **`IsSessionsEnabled = false`** — The `deferredprocessor` subscription is NOT session-enabled
- It receives a trigger message, then `DeferredMessageDispatcher` drains the matching session on the `Deferred` parking subscription

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
