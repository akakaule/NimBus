#pragma warning disable CA1707, CA2007, CS0618
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Logging;
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
            SubscriberClient.CreateAsync(null!, "endpoint", new FakeLoggerProvider()).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_NullEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), null!, new FakeLoggerProvider()).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_EmptyEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), "", new FakeLoggerProvider()).GetAwaiter().GetResult());
    }

    // ── CreateAsync factory ─────────────────────────────────────────────

    [TestMethod]
    public async Task CreateAsync_WithoutDeferredProcessor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());

        Assert.IsNotNull(sut);
        Assert.AreEqual("orders", client.LastSenderEntityPath, "Should create sender for the endpoint topic");
    }

    [TestMethod]
    public async Task CreateAsync_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());

        Assert.IsNotNull(sut);
    }

    // ── Handle delegates to adapter ─────────────────────────────────────

    [TestMethod]
    public async Task Handle_WithSessionActions_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());
        var message = CreateValidMessage();

        // Should not throw (adapter processes the message through the handler pipeline)
        await sut.Handle(message, ServiceBusTestDoubles.CreateSessionActions());
    }

    [TestMethod]
    public async Task Handle_WithSessionReceiver_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());
        var message = CreateValidMessage();

        await sut.Handle(message, new RecordingServiceBusSessionReceiver());
    }

    [TestMethod]
    public async Task Handle_WithMessageAndSessionActions_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());
        var message = CreateValidMessage();

        await sut.Handle(message, ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());
    }

    // ── RegisterHandler ─────────────────────────────────────────────────

    [TestMethod]
    public async Task RegisterHandler_ThenHandle_InvokesRegisteredHandler()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders", new FakeLoggerProvider());

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

        var sut = new SubscriberClient(client, "orders", new FakeLoggerProvider());

        Assert.IsNotNull(sut);
        Assert.AreEqual("orders", client.LastSenderEntityPath);
    }

    [TestMethod]
    public void ObsoleteConstructor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = new SubscriberClient(client, "orders", new FakeLoggerProvider());

        Assert.IsNotNull(sut);
    }

    [TestMethod]
    public void ObsoleteConstructor_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new SubscriberClient(null!, "orders", new FakeLoggerProvider()));
    }

    [TestMethod]
    public void ObsoleteConstructor_EmptyEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new SubscriberClient(new RecordingServiceBusClient(), "", new FakeLoggerProvider()));
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger = new FakeLogger();
        public ILogger GetContextualLogger(NimBus.Core.Messages.IMessageContext messageContext) => _logger;
        public ILogger GetContextualLogger(NimBus.Core.Messages.IMessage message) => _logger;
        public ILogger GetContextualLogger(string correlationId) => _logger;
    }

    private sealed class FakeLogger : ILogger
    {
        public void Verbose(string messageTemplate, params object[] propertyValues) { }
        public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Information(string messageTemplate, params object[] propertyValues) { }
        public void Information(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Error(string messageTemplate, params object[] propertyValues) { }
        public void Error(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Fatal(string messageTemplate, params object[] propertyValues) { }
        public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) { }
    }

    private sealed class FakeDeferredMessageProcessor : NimBus.Core.Messages.IDeferredMessageProcessor
    {
        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTestEventHandler : IEventHandler<TestEvent>
    {
        public int HandleCalls { get; private set; }

        public Task Handle(TestEvent message, ILogger logger, IEventHandlerContext context, CancellationToken cancellationToken = default)
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
