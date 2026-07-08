using CloudEventsInterop.Subscriber.Handlers;
using NimBus.Core.CloudEvents;
using NimBus.SDK.Extensions;

// Steps 2-3 of the demo: NimBus routes the InvoiceCreated message from SalesEndpoint's topic to
// this endpoint's own topic/subscription and tracks it, and the InvoiceCreatedHandler below
// consumes it -- reading context.GetCloudEvent() when the producer published it as a CloudEvent.
// See samples/CloudEventsInterop/README.md and docs/cloudevents.md.
var builder = Host.CreateApplicationBuilder(args);

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "InvoicingEndpoint";

        // AutoDetect handles both native NimBus messages and CloudEvents (binary or structured)
        // on the same subscription -- useful while some producers have not opted in yet. See
        // docs/cloudevents.md for the other CompatibilityMode options.
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.AutoDetect);
    },
    configureBuilder: sub => sub.AddHandlersFromAssemblyContaining<InvoiceCreatedHandler>());

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "InvoicingEndpoint";
    opts.SubscriptionName = "InvoicingEndpoint";
});

// Deferred-replay BackgroundService for the trigger subscription -- keeps parity with a
// production NimBus subscriber even though this sample does not exercise deferral.
builder.Services.AddNimBusDeferredProcessorHostedService("InvoicingEndpoint");

var host = builder.Build();
host.Run();
