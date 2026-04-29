using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.SDK;

namespace Erp.Adapter.Functions.Functions;

public class ErpEndpointFunction(ISubscriberClient subscriber)
{
    [Function("ErpEndpoint")]
    public Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "%SubscriptionName%",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ServiceBusSessionMessageActions sessionActions) =>
        subscriber.Handle(message, messageActions, sessionActions);
}
