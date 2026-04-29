using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Messages;

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
        var sessionId = message.ApplicationProperties.TryGetValue("SessionId", out var sid)
            ? sid?.ToString()
            : message.SessionId;

        if (string.IsNullOrEmpty(sessionId))
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "No SessionId");
            return;
        }

        try
        {
            await processor.ProcessDeferredMessagesAsync(sessionId, "ErpEndpoint");
            await messageActions.CompleteMessageAsync(message);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            await messageActions.CompleteMessageAsync(message);
        }
    }
}
