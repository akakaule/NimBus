#pragma warning disable CA1707, CS0618
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

        await Assert.ThrowsExactlyAsync<CreateSenderProbeException>(() =>
            sut.SendScheduledMessageAsync(message, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.AreEqual("orders", client.LastSenderEntityPath);
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

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            sut.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("payload"), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

}
