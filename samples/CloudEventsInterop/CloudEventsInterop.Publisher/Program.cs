using CloudEventsInterop.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.CloudEvents;
using NimBus.SDK;
using NimBus.SDK.Extensions;

// Step 1 of the demo: publish an InvoiceCreated event as a CloudEvents 1.0 message.
// See samples/CloudEventsInterop/README.md for the full 4-step flow and docs/cloudevents.md
// for the general publishing guide.
var builder = Host.CreateApplicationBuilder(args);

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddNimBusPublisher(options =>
{
    options.Endpoint = "SalesEndpoint";

    // Opt-in CloudEvents: this publish is emitted as a CloudEvents 1.0 binary-mode message.
    // Native routing metadata (To/From/EventTypeId/MessageId/SessionId/CorrelationId) is still
    // stamped underneath, so NimBus routing, retry, dead-lettering, and tracking are unaffected
    // -- only the wire representation gains CloudEvents attributes. Comment out UseCloudEvents
    // to see the InvoicingEndpoint subscriber handle the exact same event natively instead.
    options.UseCloudEvents(ce =>
    {
        ce.Source = new Uri("urn:cloudeventsinterop:sales");
        ce.TypeNameStrategy = CloudEventTypeNameStrategy.UnqualifiedName;
        ce.ContentMode = CloudEventContentMode.Binary;
    });
});

var host = builder.Build();

var publisher = host.Services.GetRequiredService<IPublisherClient>();

var invoice = new InvoiceCreated
{
    InvoiceId = Guid.NewGuid(),
    CustomerId = Guid.NewGuid(),
    Amount = 249.95m,
    CurrencyCode = "EUR",
};

await publisher.Publish(invoice);

Console.WriteLine($"Published InvoiceCreated {invoice.InvoiceId} as a CloudEvents 1.0 message (binary mode) to topic 'SalesEndpoint'.");
Console.WriteLine("Run CloudEventsInterop.Subscriber to see it consumed as a NimBus handler, and");
Console.WriteLine("CloudEventsInterop.NonNimBusConsumer to see the identical wire message read with no NimBus dependency.");
