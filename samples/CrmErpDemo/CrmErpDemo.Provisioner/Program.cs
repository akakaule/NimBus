using Azure.Messaging.ServiceBus.Administration;
using CrmErpDemo.Contracts;
using NimBus.ServiceBus.Provisioning;

// Partner-facing entities for the CloudEvents interop showcase (real Azure only; the
// emulator gets the same entities from EmulatorTopologyConfigBuilder):
//  - PartnerInbound topic + session-required catch-all "CrmEndpoint" subscription: the
//    external PartnerPortal publishes raw CloudEvents here, and Crm.Adapter drains it
//    with a second receiver. Sessions are required because NimBus receivers are always
//    ServiceBusSessionProcessors.
//  - PartnerPortalCapture subscription on the ErpEndpoint topic: a plain subscription
//    the NimBus-free PartnerPortal reads ERP CloudEvents from. The rule keeps only
//    original publishes (excludes response/retry/continuation envelopes).
const string partnerInboundTopic = "PartnerInbound";
const string partnerInboundSubscription = "CrmEndpoint";
const string erpTopic = "ErpEndpoint";
const string partnerCaptureSubscription = "PartnerPortalCapture";

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new CrmErpPlatformConfiguration());

Console.WriteLine("Provisioning CRM/ERP demo Service Bus topology...");
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("Topology provisioning complete.");

var adminClient = new ServiceBusAdministrationClient(connectionString);

if (!await adminClient.TopicExistsAsync(partnerInboundTopic))
{
    await adminClient.CreateTopicAsync(partnerInboundTopic);
    Console.WriteLine($"Created partner ingress topic '{partnerInboundTopic}'.");
}

if (!await adminClient.SubscriptionExistsAsync(partnerInboundTopic, partnerInboundSubscription))
{
    await adminClient.CreateSubscriptionAsync(
        new CreateSubscriptionOptions(partnerInboundTopic, partnerInboundSubscription)
        {
            RequiresSession = true,
            MaxDeliveryCount = 10,
        });
    Console.WriteLine($"Created session subscription '{partnerInboundSubscription}' on topic '{partnerInboundTopic}'.");
}

if (!await adminClient.SubscriptionExistsAsync(erpTopic, partnerCaptureSubscription))
{
    await adminClient.CreateSubscriptionAsync(
        new CreateSubscriptionOptions(erpTopic, partnerCaptureSubscription)
        {
            MaxDeliveryCount = 5,
        },
        new CreateRuleOptions(
            "cloudevents-capture",
            new SqlRuleFilter("user.MessageType = 'EventRequest' AND user.From IS NULL")));
    Console.WriteLine($"Created raw-capture subscription '{partnerCaptureSubscription}' on topic '{erpTopic}' for the PartnerPortal.");
}

Console.WriteLine("Partner interop entities provisioned.");
