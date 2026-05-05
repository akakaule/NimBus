using Azure.Messaging.ServiceBus;
using NimBus.Core.Outbox;

namespace NimBus.ServiceBus.Hosting
{
    /// <summary>
    /// A named sender used by the outbox dispatcher to send messages to the real Service Bus topic.
    /// This is separate from the OutboxSender to avoid circular resolution.
    /// </summary>
    public class OutboxDispatcherSender : Sender, INimBusDispatcherSender
    {
        public OutboxDispatcherSender(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
    }
}
