using NimBus.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// A named sender used by the outbox dispatcher to send messages to the real Service Bus topic.
    /// This is separate from the OutboxSender to avoid circular resolution.
    /// </summary>
    public class OutboxDispatcherSender : Sender
    {
        public OutboxDispatcherSender(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
    }
}
