using AspirePubSub.Subscriber.Handlers;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureServiceBusClient("servicebus");

// Register middleware pipeline — behaviors execute in registration order.
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

// Register the main subscriber (handles EventRequest messages on session-enabled subscription)
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "BillingEndpoint";
    opts.SubscriptionName = "BillingEndpoint";
});

// Deferred-replay BackgroundService for the trigger subscription. Worker hosts opt in here;
// Functions hosts would skip this and add their own [ServiceBusTrigger] function class.
builder.Services.AddNimBusDeferredProcessorHostedService("BillingEndpoint");

var host = builder.Build();
host.Run();
