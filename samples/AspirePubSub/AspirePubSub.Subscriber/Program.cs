using AspirePubSub.Subscriber.Handlers;
using NimBus.Events.Orders;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;
using NimBus.SDK.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddSingleton<NimBus.Core.Logging.ILoggerProvider>(sp =>
    new OpenTelemetryLoggerProvider(sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddNimBusSubscriber("AspireSampleEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "AspireSampleEndpoint";
    opts.SubscriptionName = "AspireSampleEndpoint";
});

var host = builder.Build();
host.Run();
