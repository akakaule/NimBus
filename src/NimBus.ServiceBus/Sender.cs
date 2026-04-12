using NimBus.Core.Messages;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus
{

    public abstract class SenderBase : ISender
    {
        private readonly ServiceBusSender _serviceBusSender;

        public SenderBase(ServiceBusSender serviceBusSender)
        {
            _serviceBusSender = serviceBusSender ?? throw new ArgumentNullException(nameof(serviceBusSender)); ;
        }

        public string TopicName => _serviceBusSender.EntityPath;

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
             _serviceBusSender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay), cancellationToken);

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            _serviceBusSender.SendMessagesAsync(messages.Select(message => MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay)).ToList(), cancellationToken);

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            _serviceBusSender.ScheduleMessageAsync(MessageHelper.ToServiceBusMessage(message), scheduledEnqueueTime, cancellationToken);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            _serviceBusSender.CancelScheduledMessageAsync(sequenceNumber, cancellationToken);
    }

    public class Sender : SenderBase
    {
        public Sender(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
    }

    public class SenderManager : SenderBase
    {
        public SenderManager(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
    }
}
