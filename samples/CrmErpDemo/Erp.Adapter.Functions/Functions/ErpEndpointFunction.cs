using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.ServiceBus;

namespace Erp.Adapter.Functions.Functions;

public class ErpEndpointFunction(IServiceBusAdapter adapter)
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
        adapter.Handle(message, messageActions, sessionActions);
}
