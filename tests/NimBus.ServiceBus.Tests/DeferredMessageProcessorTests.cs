#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class DeferredMessageProcessorTests
{
    // ── Constructor ─────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new DeferredMessageProcessor(null!));
    }

    [TestMethod]
    public void Constructor_DefaultSubscriptionName_UsesConstant()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client);

        // Verify it doesn't throw and can be used
        Assert.IsNotNull(sut);
    }

    // ── Argument validation ─────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_NullSessionId_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync(null!, "my-topic"));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptySessionId_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("", "my-topic"));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_NullTopicName_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", null!));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptyTopicName_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", ""));
    }

    // ── SessionCannotBeLocked ───────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_SessionCannotBeLocked_ReturnsGracefully()
    {
        var client = new RecordingServiceBusClient
        {
            AcceptSessionException = new ServiceBusException("no session", ServiceBusFailureReason.SessionCannotBeLocked)
        };
        var sut = new DeferredMessageProcessor(client);

        // Should not throw
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");
    }

    // ── Empty batch ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptyBatch_SendsNothingAndCompletes()
    {
        var client = new RecordingServiceBusClient();
        // No batches configured → receiver returns empty list on first call
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual(0, client.Sender.SentMessages.Count);
        Assert.AreEqual(0, client.SessionReceiver.CompletedMessages.Count);
    }

    // ── Processing ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_SingleBatch_SortsByDeferralSequenceAndRepublishes()
    {
        var client = new RecordingServiceBusClient();
        var msg3 = CreateReceivedMessage("corr-3", deferralSequence: 3);
        var msg1 = CreateReceivedMessage("corr-1", deferralSequence: 1);
        var msg2 = CreateReceivedMessage("corr-2", deferralSequence: 2);

        // Messages arrive out of order
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg3, msg1, msg2 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        // Should be republished in DeferralSequence order: 1, 2, 3
        Assert.AreEqual(3, client.Sender.SentMessages.Count);
        Assert.AreEqual("corr-1", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-2", client.Sender.SentMessages[1].CorrelationId);
        Assert.AreEqual("corr-3", client.Sender.SentMessages[2].CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CompletesAllDeferredMessages()
    {
        var client = new RecordingServiceBusClient();
        var msg1 = CreateReceivedMessage("corr-1", deferralSequence: 1);
        var msg2 = CreateReceivedMessage("corr-2", deferralSequence: 2);
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg1, msg2 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual(2, client.SessionReceiver.CompletedMessages.Count);
        Assert.AreSame(msg1, client.SessionReceiver.CompletedMessages[0]);
        Assert.AreSame(msg2, client.SessionReceiver.CompletedMessages[1]);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_ExcludesDeferredProperties()
    {
        var client = new RecordingServiceBusClient();
        var msg = CreateReceivedMessage("corr-1", deferralSequence: 5, extraProps: new Dictionary<string, object>
        {
            { UserPropertyName.To.ToString(), "AnalyticsEndpoint" },
            { UserPropertyName.EventId.ToString(), "event-1" },
        });
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.IsFalse(republished.ApplicationProperties.ContainsKey(UserPropertyName.OriginalSessionId.ToString()),
            "OriginalSessionId should be excluded");
        Assert.IsFalse(republished.ApplicationProperties.ContainsKey(UserPropertyName.DeferralSequence.ToString()),
            "DeferralSequence should be excluded");
        Assert.AreEqual("AnalyticsEndpoint", republished.ApplicationProperties[UserPropertyName.To.ToString()]);
        Assert.AreEqual("event-1", republished.ApplicationProperties[UserPropertyName.EventId.ToString()]);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_SetsSessionIdAndCorrelationId()
    {
        var client = new RecordingServiceBusClient();
        var msg = CreateReceivedMessage("corr-1", deferralSequence: 1);
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.AreEqual("session-1", republished.SessionId);
        Assert.AreEqual("corr-1", republished.CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_AcceptsCorrectSession()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-42", "orders");

        Assert.AreEqual("orders", client.LastTopicName);
        Assert.AreEqual(Constants.DeferredSubscriptionName, client.LastSubscriptionName);
        Assert.AreEqual("session-42", client.LastSessionId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CustomSubscriptionName_UsesProvidedName()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client, "CustomDeferred");

        await sut.ProcessDeferredMessagesAsync("session-1", "orders");

        Assert.AreEqual("CustomDeferred", client.LastSubscriptionName);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CreatesPublisherForCorrectTopic()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage>
        {
            CreateReceivedMessage("corr-1", deferralSequence: 1)
        });
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        Assert.AreEqual("billing", client.LastSenderEntityPath);
    }

    // ── GetDeferralSequence behavior (tested via sort order) ────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_DeferralSequenceAsString_SortsCorrectly()
    {
        var client = new RecordingServiceBusClient();
        // DeferralSequence stored as string instead of int
        var msg2 = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-2",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), "2" },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });
        var msg1 = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-1",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), "1" },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });

        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg2, msg1 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual("corr-1", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-2", client.Sender.SentMessages[1].CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_MissingDeferralSequence_TreatsAsZero()
    {
        var client = new RecordingServiceBusClient();
        // One message with sequence 1, one with no sequence (should be treated as 0)
        var msgWithSeq = CreateReceivedMessage("corr-with-seq", deferralSequence: 1);
        var msgWithout = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-no-seq",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.To.ToString(), "SomeEndpoint" },
            });

        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msgWithSeq, msgWithout });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        // Missing sequence treated as 0, so it should come first
        Assert.AreEqual("corr-no-seq", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-with-seq", client.Sender.SentMessages[1].CorrelationId);
    }

    // ── Transient exception ─────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_TransientServiceBusException_ThrowsTransientException()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveMessagesException =
            new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var sut = new DeferredMessageProcessor(client);

        var ex = await Assert.ThrowsExceptionAsync<TransientException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", "my-topic"));

        Assert.IsInstanceOfType(ex.InnerException, typeof(ServiceBusException));
    }

    // ── Body preservation ───────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_PreservesBody()
    {
        var client = new RecordingServiceBusClient();
        var body = new BinaryData("{\"orderId\":42}");
        var msg = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: body,
            correlationId: "corr-1",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), 1 },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.AreEqual("{\"orderId\":42}", republished.Body.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ServiceBusReceivedMessage CreateReceivedMessage(
        string correlationId,
        int deferralSequence,
        Dictionary<string, object> extraProps = null)
    {
        var properties = new Dictionary<string, object>
        {
            { UserPropertyName.DeferralSequence.ToString(), deferralSequence },
            { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
        };

        if (extraProps != null)
        {
            foreach (var kvp in extraProps)
                properties[kvp.Key] = kvp.Value;
        }

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: correlationId,
            properties: properties);
    }
}
