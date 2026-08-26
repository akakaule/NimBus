using AspirePubSub.WarehouseSubscriber.Handlers;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;

// Warehouse runs in its own process: AddNimBusSubscriber allows one endpoint per
// container, because ISubscriberClient is a non-keyed singleton. This adapter is
// deliberately unreliable (see FlakyWarehouse) so the sample always has failures
// to retry, resubmit and group, while Billing stays healthy next to it.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber("WarehouseEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<WarehouseOrderPlacedHandler>();
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "WarehouseEndpoint";
    opts.SubscriptionName = "WarehouseEndpoint";
});

builder.Services.AddNimBusDeferredProcessorHostedService("WarehouseEndpoint");

var host = builder.Build();
host.Run();
