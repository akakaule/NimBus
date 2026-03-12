#pragma warning disable CA1707
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.SDK;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class SdkAndManagerTests
{
    [TestMethod]
    public async Task ManagerClient_Resubmit_SendsManagerMarkedMessageToEndpointSender()
    {
        var client = new RecordingServiceBusClient();
        var sut = new ManagerClient(client);
        var errorResponse = new MessageEntity
        {
            CorrelationId = "correlation-1",
            EventId = "event-1",
            SessionId = "session-1",
            MessageId = "message-1",
            OriginatingMessageId = "originating-1",
        };

        await sut.Resubmit(errorResponse, "billing", "OrderPlaced", "{\"id\":1}");

        Assert.AreEqual("billing", client.LastSenderEntityPath);
        Assert.AreEqual(1, client.Sender.SentMessages.Count);

        var sentMessage = client.Sender.SentMessages.Single();
        Assert.AreEqual("billing", sentMessage.ApplicationProperties["To"]);
        Assert.AreEqual(Constants.ManagerId, sentMessage.ApplicationProperties["From"]);
        Assert.AreEqual("OrderPlaced", sentMessage.ApplicationProperties["EventTypeId"]);
        Assert.AreEqual(MessageType.ResubmissionRequest.ToString(), sentMessage.ApplicationProperties["MessageType"]);
        Assert.AreEqual("originating-1", sentMessage.ApplicationProperties["OriginatingMessageId"]);
        Assert.AreEqual("message-1", sentMessage.ApplicationProperties["ParentMessageId"]);
        Assert.AreEqual("session-1", sentMessage.SessionId);
    }

    [TestMethod]
    public async Task ManagerClient_Skip_SendsManagerMarkedMessageToEndpointSender()
    {
        var client = new RecordingServiceBusClient();
        var sut = new ManagerClient(client);
        var errorResponse = new MessageEntity
        {
            EventId = "event-1",
            SessionId = "session-1",
            MessageId = "message-1",
            OriginatingMessageId = "originating-1",
        };

        await sut.Skip(errorResponse, "billing", "OrderPlaced");

        Assert.AreEqual("billing", client.LastSenderEntityPath);
        Assert.AreEqual(1, client.Sender.SentMessages.Count);

        var sentMessage = client.Sender.SentMessages.Single();
        Assert.AreEqual("billing", sentMessage.ApplicationProperties["To"]);
        Assert.AreEqual(Constants.ManagerId, sentMessage.ApplicationProperties["From"]);
        Assert.AreEqual("OrderPlaced", sentMessage.ApplicationProperties["EventTypeId"]);
        Assert.AreEqual(MessageType.SkipRequest.ToString(), sentMessage.ApplicationProperties["MessageType"]);
        Assert.AreEqual("originating-1", sentMessage.ApplicationProperties["OriginatingMessageId"]);
        Assert.AreEqual("message-1", sentMessage.ApplicationProperties["ParentMessageId"]);
        Assert.AreEqual("session-1", sentMessage.SessionId);
        Assert.AreEqual("message-1", sentMessage.CorrelationId);
    }

    [TestMethod]
    public void PublisherClient_PrivateMessageFactory_UsesDeterministicMessageIdForEquivalentPayloads()
    {
        var first = new TestEvent { SessionIdValue = "session-1", Payload = "same" };
        var second = new TestEvent { SessionIdValue = "session-1", Payload = "same" };
        var getMessageStatic = typeof(PublisherClient).GetMethod("GetMessageStatic", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(getMessageStatic);

        var firstMessage = (IMessage)getMessageStatic.Invoke(null, new object[] { first, "correlation-1", null!, "session-1" })!;
        var secondMessage = (IMessage)getMessageStatic.Invoke(null, new object[] { second, "correlation-2", null!, "session-1" })!;

        Assert.AreEqual(firstMessage.MessageId, secondMessage.MessageId);
        Assert.AreEqual("TestEvent", firstMessage.EventTypeId);
        Assert.AreEqual("TestEvent", firstMessage.To);
        Assert.AreEqual("session-1", firstMessage.SessionId);
    }

    [TestMethod]
    public void PublisherClient_GetBatchesStatic_WithOversizedEvent_YieldsSingleItemBatch()
    {
        var events = new List<NimBus.Core.Events.IEvent>
        {
            new TestEvent { Payload = new string('x', 80_000) }
        };

        var batches = PublisherClient.GetBatchesStatic(events).ToList();

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(1, batches[0].Count());
    }
}
