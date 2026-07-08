using System.Text;
using Azure.Messaging.ServiceBus;

// Step 4 of the demo: consume the SAME message the NimBus publisher sent in step 1, using only
// Azure.Messaging.ServiceBus -- no NimBus package reference anywhere in this project. This reads
// from "RawCloudEventsCapture", a plain subscription CloudEventsInterop.Provisioner creates on
// the SalesEndpoint topic (the topic the publisher sends the original message to, before any
// NimBus forward/rewrite rule runs), so the message here is byte-identical to what NimBus wrote
// to the wire. See samples/CloudEventsInterop/README.md and docs/cloudevents.md.
const string topicName = "SalesEndpoint";
const string subscriptionName = "RawCloudEventsCapture";
const string structuredContentType = "application/cloudevents+json";
const string cloudEventsPropertyPrefix = "cloudEvents:";

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__servicebus is required -- a Service Bus namespace connection string " +
        "(or 'Endpoint=sb://localhost;...;UseDevelopmentEmulator=true' for the local emulator).");

await using var client = new ServiceBusClient(connectionString);
await using var receiver = client.CreateReceiver(topicName, subscriptionName);

Console.WriteLine($"Listening on {topicName}/{subscriptionName} as a plain Azure.Messaging.ServiceBus consumer (no NimBus reference). Ctrl+C to stop.");

while (true)
{
    var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));
    if (message is null)
    {
        Console.WriteLine("No message received in 30s, still waiting...");
        continue;
    }

    if (string.Equals(message.ContentType, structuredContentType, StringComparison.OrdinalIgnoreCase))
    {
        // Structured content mode: the entire CloudEvent (context attributes + data) is the
        // JSON body.
        Console.WriteLine("Structured CloudEvent received:");
        Console.WriteLine(message.Body.ToString());
    }
    else
    {
        // Binary content mode: CloudEvents context attributes ride as "cloudEvents:*"
        // application properties (the AMQP CloudEvents binding); the body is the raw
        // domain-event JSON and message.ContentType carries datacontenttype.
        Console.WriteLine($"Binary CloudEvent received (datacontenttype={message.ContentType}):");
        foreach (var property in message.ApplicationProperties)
        {
            if (property.Key.StartsWith(cloudEventsPropertyPrefix, StringComparison.Ordinal))
                Console.WriteLine($"  {property.Key} = {property.Value}");
        }
        Console.WriteLine($"  data = {Encoding.UTF8.GetString(message.Body)}");
    }

    await receiver.CompleteMessageAsync(message);
}
