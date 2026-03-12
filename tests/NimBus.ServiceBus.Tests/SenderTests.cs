#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class SenderTests
{
    // ── Constructor ─────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullServiceBusSender_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new Sender(null!));
    }

    [TestMethod]
    public void SenderManager_Constructor_NullServiceBusSender_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new SenderManager(null!));
    }

    // ── Send single ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Send_SingleMessage_DelegatesToServiceBusSender()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var message = CreateMessage("Billing");

        await sut.Send(message);

        Assert.AreEqual(1, sbSender.SentMessages.Count);
        Assert.AreEqual("Billing", sbSender.SentMessages[0].ApplicationProperties[UserPropertyName.To.ToString()]);
    }

    [TestMethod]
    public async Task Send_SingleMessage_WithDelay_SetsScheduledEnqueueTime()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var message = CreateMessage("Billing");
        var beforeSend = DateTime.UtcNow;

        await sut.Send(message, messageEnqueueDelay: 5);

        var sent = sbSender.SentMessages.Single();
        // ScheduledEnqueueTime should be ~5 minutes from now
        Assert.IsTrue(sent.ScheduledEnqueueTime >= beforeSend.AddMinutes(4),
            $"ScheduledEnqueueTime {sent.ScheduledEnqueueTime:O} should be at least 4 minutes from {beforeSend:O}");
    }

    // ── Send batch ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Send_BatchMessages_AllAreSent()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var messages = new List<IMessage>
        {
            CreateMessage("Billing"),
            CreateMessage("Analytics"),
        };

        await sut.Send(messages);

        Assert.AreEqual(2, sbSender.SentMessages.Count);
    }

    // ── TopicName ───────────────────────────────────────────────────────

    [TestMethod]
    public void TopicName_DelegatesToServiceBusSenderEntityPath()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);

        // RecordingServiceBusSender has no real connection, so EntityPath is null.
        // Verify TopicName delegates without throwing.
        var topicName = sut.TopicName;
        Assert.AreEqual(sbSender.EntityPath, topicName);
    }

    // ── SenderManager ───────────────────────────────────────────────────

    [TestMethod]
    public async Task SenderManager_Send_DelegatesToServiceBusSender()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new SenderManager(sbSender);
        var message = CreateMessage("Billing");

        await sut.Send(message);

        Assert.AreEqual(1, sbSender.SentMessages.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IMessage CreateMessage(string to)
    {
        return new Message
        {
            To = to,
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" }
            },
        };
    }
}
