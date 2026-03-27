using AspirePubSub.Subscriber;
using AspirePubSub.Subscriber.Handlers;
using NimBus.Core.Messages;
using NimBus.Events.Orders;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;
using NimBus.SDK.Logging;
using NimBus.ServiceBus;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddSingleton<NimBus.Core.Logging.ILoggerProvider>(sp =>
    new OpenTelemetryLoggerProvider(sp.GetRequiredService<ILoggerFactory>()));

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

// Register the separated DeferredProcessor (handles ProcessDeferredRequest on non-session subscription)
builder.Services.AddSingleton<IDeferredMessageProcessor>(sp =>
{
    var sbClient = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
    return new DeferredMessageProcessor(sbClient);
});

builder.Services.AddHostedService(sp =>
{
    var sbClient = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
    var processor = sp.GetRequiredService<IDeferredMessageProcessor>();
    var logger = sp.GetRequiredService<ILogger<DeferredProcessorService>>();
    return new DeferredProcessorService(sbClient, processor, logger, "BillingEndpoint");
});

var host = builder.Build();
host.Run();
