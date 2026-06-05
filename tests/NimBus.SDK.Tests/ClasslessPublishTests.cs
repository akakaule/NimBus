#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class ClasslessPublishTests
{
    private sealed class CapturingSender : ISender
    {
        public readonly List<IMessage> Sent = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [TestMethod]
    public async Task Publish_classless_message_sends_it_unchanged()
    {
        var sender = new CapturingSender();
        var publisher = new PublisherClient(sender);
        var msg = new Message
        {
            To = "crm.contact.enriched.v1",
            EventTypeId = "crm.contact.enriched.v1",
            SessionId = "s1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "crm.contact.enriched.v1",
                    EventJson = "{\"x\":1}",
                },
            },
        };

        await publisher.Publish(msg);

        Assert.AreEqual(1, sender.Sent.Count);
        Assert.AreEqual("crm.contact.enriched.v1", sender.Sent[0].EventTypeId);
    }
}
