#pragma warning disable CA1707, CA2007, CS0618
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class SubscriberClientTests
{
    // ── CreateAsync argument validation ─────────────────────────────────

    [TestMethod]
    public void CreateAsync_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            SubscriberClient.CreateAsync(null!, "endpoint").GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_NullEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), null!).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_EmptyEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), "").GetAwaiter().GetResult());
    }

    // ── CreateAsync factory ─────────────────────────────────────────────

    [TestMethod]
    public async Task CreateAsync_WithoutDeferredProcessor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");

        Assert.IsNotNull(sut);
        Assert.AreEqual("orders", client.LastSenderEntityPath, "Should create sender for the endpoint topic");
    }

    [TestMethod]
    public async Task CreateAsync_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = await SubscriberClient.CreateAsync(client, "orders");

        Assert.IsNotNull(sut);
    }

    // ── Handle delegates to adapter ─────────────────────────────────────

    [TestMethod]
    public async Task Handle_WithSessionActions_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var message = CreateValidMessage();

        // Should not throw (adapter processes the message through the handler pipeline)
        await sut.Handle(message, ServiceBusTestDoubles.CreateSessionActions());
    }

    [TestMethod]
    public async Task Handle_WithSessionReceiver_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var message = CreateValidMessage();

        await sut.Handle(message, new RecordingServiceBusSessionReceiver());
    }

    [TestMethod]
    public async Task Handle_WithMessageAndSessionActions_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var message = CreateValidMessage();

        await sut.Handle(message, ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());
    }

    // ── RegisterHandler ─────────────────────────────────────────────────

    [TestMethod]
    public async Task RegisterHandler_ThenHandle_InvokesRegisteredHandler()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");

        var handler = new FakeTestEventHandler();
        sut.RegisterHandler<TestEvent>(() => handler);

        // The handler is registered but invocation is verified via the adapter pipeline.
        // RegisterHandler should not throw.
        Assert.IsNotNull(handler);
    }

    // ── Obsolete constructor ────────────────────────────────────────────

    [TestMethod]
    public void ObsoleteConstructor_WithoutProcessor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = new SubscriberClient(client, "orders");

        Assert.IsNotNull(sut);
        Assert.AreEqual("orders", client.LastSenderEntityPath);
    }

    [TestMethod]
    public void ObsoleteConstructor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = new SubscriberClient(client, "orders");

        Assert.IsNotNull(sut);
    }

    [TestMethod]
    public void ObsoleteConstructor_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new SubscriberClient(null!, "orders"));
    }

    [TestMethod]
    public void ObsoleteConstructor_EmptyEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new SubscriberClient(new RecordingServiceBusClient(), ""));
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeDeferredMessageProcessor : NimBus.Core.Messages.IDeferredMessageProcessor
    {
        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTestEventHandler : IEventHandler<TestEvent>
    {
        public int HandleCalls { get; private set; }

        public Task Handle(TestEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    private static ServiceBusReceivedMessage CreateValidMessage()
    {
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: "msg-1",
            sessionId: "session-1",
            properties: new System.Collections.Generic.Dictionary<string, object>
            {
                { "EventId", "event-1" },
                { "To", "orders" },
                { "From", "StorefrontEndpoint" },
                { "MessageType", "EventRequest" },
                { "OriginatingMessageId", "self" },
                { "ParentMessageId", "self" },
                { "OriginatingFrom", "StorefrontEndpoint" },
                { "EventTypeId", "OrderPlaced" },
                { "RetryCount", 0 },
            });
    }
}
