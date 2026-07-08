using Azure.Messaging.ServiceBus.Administration;
using CloudEventsInterop.Contracts;
using NimBus.ServiceBus.Provisioning;

// Name of the plain (non-NimBus) subscription created on the SalesEndpoint topic -- the topic
// the publisher sends the original message to, before any NimBus forward/rewrite rule touches
// it. A subscription's default rule ("$Default") matches every message published to the topic,
// so this subscription receives an untouched copy of the exact wire message NimBus published:
// same MessageId, same body, same application properties (including "cloudEvents:*" when the
// publisher has CloudEvents enabled). This is what CloudEventsInterop.NonNimBusConsumer reads
// from step 4 of the demo -- see samples/CloudEventsInterop/README.md.
const string rawCaptureSubscription = "RawCloudEventsCapture";

// NimBus topic names are the endpoint class name (Endpoint.Id) -- see SalesEndpoint in
// CloudEventsInterop.Contracts.
const string salesTopic = "SalesEndpoint";

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__servicebus is required -- a Service Bus namespace connection string with Manage rights " +
        "(or 'Endpoint=sb://localhost;...;UseDevelopmentEmulator=true' for the local emulator).");

Console.WriteLine("Provisioning NimBus topology (SalesEndpoint -> InvoicingEndpoint)...");
var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new SamplePlatform());
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("NimBus topology provisioning complete.");

var adminClient = new ServiceBusAdministrationClient(connectionString);
if (!await adminClient.SubscriptionExistsAsync(salesTopic, rawCaptureSubscription))
{
    await adminClient.CreateSubscriptionAsync(
        new CreateSubscriptionOptions(salesTopic, rawCaptureSubscription)
        {
            MaxDeliveryCount = 5,
        });
    Console.WriteLine($"Created raw-capture subscription '{rawCaptureSubscription}' on topic '{salesTopic}' for the non-NimBus consumer.");
}
else
{
    Console.WriteLine($"Raw-capture subscription '{rawCaptureSubscription}' already exists.");
}

Console.WriteLine("Done. Run the Publisher, Subscriber, and NonNimBusConsumer projects next.");
