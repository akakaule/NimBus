using AspirePubSub.Subscriber;
using AspirePubSub.Subscriber.Handlers;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Pipeline;
using NimBus.Events.Orders;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

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
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "BillingEndpoint";
    opts.SubscriptionName = "BillingEndpoint";
});

// Hosted service that drives the deferred replay loop. The IDeferredMessageProcessor
// itself is registered by AddNimBusSubscriber above.
builder.Services.AddHostedService(sp =>
{
    var sbClient = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
    var processor = sp.GetRequiredService<IDeferredMessageProcessor>();
    var logger = sp.GetRequiredService<ILogger<DeferredProcessorService>>();
    return new DeferredProcessorService(sbClient, processor, logger, "BillingEndpoint");
});

var host = builder.Build();
host.Run();
