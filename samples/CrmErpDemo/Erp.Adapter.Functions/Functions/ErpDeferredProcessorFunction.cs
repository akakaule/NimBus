using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

namespace Erp.Adapter.Functions.Functions;

public class ErpDeferredProcessorFunction(IDeferredMessageProcessor processor)
{
    [Function("ErpDeferredProcessor")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "deferredprocessor",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "ErpEndpoint");

        if (outcome.Action == DeferredMessageDispatchAction.DeadLetter)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: outcome.DeadLetterReason);
        }
        else
        {
            await messageActions.CompleteMessageAsync(message);
        }
    }
}
