using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.SDK;

namespace DataPlatform.Adapter.Functions.Functions;

/// <summary>
/// Azure Functions ServiceBus trigger — the bridge the Functions runtime
/// uses to actually pull messages off the DataPlatformEndpoint subscription
/// and hand them to NimBus's <see cref="ISubscriberClient"/>, which then
/// dispatches to the registered event handlers.
///
/// Without this class messages stack up in `Pending` forever because the
/// Worker host has nothing to invoke. Topic/Subscription names come from
/// the same %TopicName% / %SubscriptionName% app settings the Erp adapter
/// uses; sessions stay enabled because the events carry [SessionKey] —
/// ErpCustomerCreated is keyed on AccountId.
/// </summary>
public class DataPlatformEndpointFunction(ISubscriberClient subscriber)
{
    [Function("DataPlatformEndpoint")]
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
