using AspirePubSub.Subscriber.Handlers;
using Microsoft.Extensions.Azure;
using NimBus.Events.Orders;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddAzureClients(clients =>
{
    clients.AddServiceBusClient(builder.Configuration.GetConnectionString("servicebus"));
});

builder.Services.AddNimBusSubscriber("nimbus", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "nimbus";
    opts.SubscriptionName = "sample-subscriber";
});

var host = builder.Build();
host.Run();
