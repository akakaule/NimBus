#pragma warning disable CA1707
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.ServiceBus;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusSessionTests
{
    [TestMethod]
    public async Task SendScheduledMessageAsync_WithTopicSubscriptionEntityPath_UsesTopicSender()
    {
        var client = new RecordingServiceBusClient
        {
            CreateSenderException = new CreateSenderProbeException("sender-path-recorded")
        };
        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            client,
            "orders/subscription-a",
            "session-1");
        var message = new Azure.Messaging.ServiceBus.ServiceBusMessage("payload");

        await Assert.ThrowsExceptionAsync<CreateSenderProbeException>(() =>
            sut.SendScheduledMessageAsync(message, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.AreEqual("orders", client.LastSenderEntityPath);
    }

    [TestMethod]
    public async Task ReceiveDeferredMessageAsync_WithTopicSubscriptionEntityPath_AcceptsTopicSubscriptionSession()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.DeferredMessagesToReturn = new[]
        {
            (ServiceBusReceivedMessage)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusReceivedMessage))
        };

        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            client,
            "orders/subscription-a",
            "session-1");

        var deferred = await sut.ReceiveDeferredMessageAsync(42);

        Assert.IsNotNull(deferred);
        Assert.AreEqual("orders", client.LastTopicName);
        Assert.AreEqual("subscription-a", client.LastSubscriptionName);
        Assert.AreEqual("session-1", client.LastSessionId);
        CollectionAssert.AreEqual(new long[] { 42 }, client.SessionReceiver.LastDeferredSequenceNumbers.ToArray());
    }

    [TestMethod]
    public async Task ReceiveDeferredMessageAsync_WithQueueEntityPath_AcceptsQueueSession()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.DeferredMessagesToReturn = new[]
        {
            (ServiceBusReceivedMessage)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusReceivedMessage))
        };

        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            client,
            "orders",
            "session-1");

        await sut.ReceiveDeferredMessageAsync(7);

        Assert.AreEqual("orders", client.LastQueueName);
        Assert.AreEqual("session-1", client.LastSessionId);
        Assert.IsNull(client.LastTopicName);
        Assert.IsNull(client.LastSubscriptionName);
    }

    [TestMethod]
    public async Task SendScheduledMessageAsync_WithoutClientOrEntityPath_ThrowsInvalidOperationException()
    {
        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            serviceBusClient: null!,
            entityPath: null!,
            sessionId: "session-1");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            sut.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("payload"), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public async Task ReceiveDeferredMessageAsync_WithoutClientOrEntityPath_ThrowsInvalidOperationException()
    {
        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            serviceBusClient: null!,
            entityPath: null!,
            sessionId: "session-1");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => sut.ReceiveDeferredMessageAsync(5));
    }
}
