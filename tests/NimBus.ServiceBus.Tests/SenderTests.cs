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
        Assert.ThrowsExactly<ArgumentNullException>(() => new Sender(null!));
    }

    [TestMethod]
    public void SenderManager_Constructor_NullServiceBusSender_ThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SenderManager(null!));
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

    // ── Scheduled-message handle overloads (spec 025) ───────────────────

    [TestMethod]
    public async Task ScheduleMessageWithHandle_ReturnsBrokerSequenceHandle()
    {
        var sbSender = new RecordingServiceBusSender { NextSequenceNumber = 42L };
        var sut = new Sender(sbSender);
        var message = (Message)CreateMessage("Billing");
        message.ScheduledMessageId = "timeout-1";
        var due = DateTimeOffset.UtcNow.AddMinutes(30);

        var handle = await ((ISender)sut).ScheduleMessageWithHandle(message, due);

        Assert.AreEqual("timeout-1", handle.TimeoutId);
        Assert.AreEqual(42L, handle.SequenceNumber);
        Assert.AreEqual(ScheduledMessageHandleKind.BrokerSequenceNumber, handle.Kind);
        Assert.AreEqual(due, sbSender.ScheduledMessages.Single().ScheduledEnqueueTime);
    }

    [TestMethod]
    public async Task ScheduleMessageWithHandle_NoMarker_FallsBackToMessageId()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var message = (Message)CreateMessage("Billing");
        message.MessageId = "deterministic-1";

        var handle = await ((ISender)sut).ScheduleMessageWithHandle(message, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.AreEqual("deterministic-1", handle.TimeoutId);
    }

    [TestMethod]
    public async Task CancelScheduledMessage_Handle_DelegatesSequenceOnlyAndReturnsCancellationRequested()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var handle = new ScheduledMessageHandle("timeout-1", 42L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        var outcome = await ((ISender)sut).CancelScheduledMessage(handle);

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancellationRequested, outcome);
        Assert.AreEqual(42L, sbSender.CancelledSequenceNumbers.Single());
    }

    [TestMethod]
    public async Task CancelScheduledMessage_Handle_MismatchedTimeoutId_StillCancelsSuppliedSequence()
    {
        // Documented best effort: the broker API is sequence-only; direct mode
        // validates shape but CANNOT verify the TimeoutId↔sequence pairing.
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var handle = new ScheduledMessageHandle("some-other-timeout", 7L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        var outcome = await ((ISender)sut).CancelScheduledMessage(handle);

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancellationRequested, outcome);
        Assert.AreEqual(7L, sbSender.CancelledSequenceNumbers.Single());
    }

    [TestMethod]
    public async Task CancelScheduledMessage_OutboxKindHandle_IsRejectedNotReinterpreted()
    {
        var sbSender = new RecordingServiceBusSender();
        var sut = new Sender(sbSender);
        var handle = new ScheduledMessageHandle("timeout-1", 42L, ScheduledMessageHandleKind.SqlOutboxSequenceNumber);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((ISender)sut).CancelScheduledMessage(handle));
        Assert.AreEqual(0, sbSender.CancelledSequenceNumbers.Count);
    }

    [TestMethod]
    public async Task CancelScheduledMessage_NonPositiveSequence_IsRejected()
    {
        var sut = new Sender(new RecordingServiceBusSender());
        var handle = new ScheduledMessageHandle("timeout-1", 0L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => ((ISender)sut).CancelScheduledMessage(handle));
    }

    [TestMethod]
    public async Task CancelScheduledMessage_BlankTimeoutId_IsRejected()
    {
        var sut = new Sender(new RecordingServiceBusSender());
        var handle = new ScheduledMessageHandle(" ", 42L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => ((ISender)sut).CancelScheduledMessage(handle));
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
