#pragma warning disable CA1707, CA2007, CS0618
using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class SubscriberClientTests
{
    // ── Constructor argument validation ─────────────────────────────────

    [TestMethod]
    public void Constructor_NullMessageHandler_Throws()
    {
        AssertCtorThrowsArgumentNull(null, new RecordingServiceBusAdapter(), new EventHandlerProvider());
    }

    [TestMethod]
    public void Constructor_NullServiceBusAdapter_Throws()
    {
        AssertCtorThrowsArgumentNull(new RecordingMessageHandler(), null, new EventHandlerProvider());
    }

    [TestMethod]
    public void Constructor_NullEventHandlerProvider_Throws()
    {
        AssertCtorThrowsArgumentNull(new RecordingMessageHandler(), new RecordingServiceBusAdapter(), null);
    }

    private static void AssertCtorThrowsArgumentNull(
        IMessageHandler? messageHandler,
        global::NimBus.ServiceBus.IServiceBusAdapter? serviceBusAdapter,
        EventHandlerProvider? eventHandlerProvider)
    {
        var ctor = typeof(SubscriberClient).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        try
        {
            ctor.Invoke(new object?[] { messageHandler, serviceBusAdapter, eventHandlerProvider });
            Assert.Fail("Expected ArgumentNullException, but no exception was thrown.");
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is ArgumentNullException)
        {
            // expected
        }
    }

    // ── IMessageHandler delegation ──────────────────────────────────────

    [TestMethod]
    public async Task Handle_IMessageContext_DelegatesToInnerMessageHandler()
    {
        var inner = new RecordingMessageHandler();
        var sut = CreateSubscriber(messageHandler: inner);

        // The recording handler doesn't read the context, so a null reference
        // is fine here — the assertion is purely on the delegation count.
        await sut.Handle((IMessageContext)null!);

        Assert.AreEqual(1, inner.HandleCalls);
    }

    // ── IServiceBusAdapter delegation (Obsolete bridges) ────────────────

    [TestMethod]
    public async Task Handle_WithSessionActions_DelegatesToServiceBusAdapter()
    {
        var sba = new RecordingServiceBusAdapter();
        var sut = CreateSubscriber(serviceBusAdapter: sba);

        var msg = CreateValidMessage();
        await sut.Handle(msg, ServiceBusTestDoubles.CreateSessionActions());

        Assert.AreEqual(1, sba.SessionActionsCalls);
    }

    [TestMethod]
    public async Task Handle_WithMessageAndSessionActions_DelegatesToServiceBusAdapter()
    {
        var sba = new RecordingServiceBusAdapter();
        var sut = CreateSubscriber(serviceBusAdapter: sba);

        var msg = CreateValidMessage();
        await sut.Handle(msg, ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());

        Assert.AreEqual(1, sba.MessageActionsCalls);
    }

    [TestMethod]
    public async Task Handle_WithSessionReceiver_DelegatesToServiceBusAdapter()
    {
        var sba = new RecordingServiceBusAdapter();
        var sut = CreateSubscriber(serviceBusAdapter: sba);

        var msg = CreateValidMessage();
        await sut.Handle(msg, new RecordingServiceBusSessionReceiver());

        Assert.AreEqual(1, sba.SessionReceiverCalls);
    }

    // ── RegisterHandler ─────────────────────────────────────────────────

    [TestMethod]
    public void RegisterHandler_DoesNotThrow()
    {
        var sut = CreateSubscriber();
        sut.RegisterHandler<TestEvent>(() => new FakeTestEventHandler());
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SubscriberClient CreateSubscriber(
        IMessageHandler? messageHandler = null,
        global::NimBus.ServiceBus.IServiceBusAdapter? serviceBusAdapter = null,
        EventHandlerProvider? eventHandlerProvider = null)
    {
        var ctorType = typeof(SubscriberClient);
        var ctor = ctorType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (SubscriberClient)ctor.Invoke(new object?[]
        {
            messageHandler ?? new RecordingMessageHandler(),
            serviceBusAdapter ?? new RecordingServiceBusAdapter(),
            eventHandlerProvider ?? new EventHandlerProvider(),
        });
    }

    private sealed class RecordingMessageHandler : IMessageHandler
    {
        public int HandleCalls { get; private set; }

        public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingServiceBusAdapter : global::NimBus.ServiceBus.IServiceBusAdapter
    {
        public int SessionActionsCalls { get; private set; }
        public int MessageActionsCalls { get; private set; }
        public int SessionReceiverCalls { get; private set; }
        public int ProcessSessionArgsCalls { get; private set; }

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
        {
            SessionActionsCalls++;
            return Task.CompletedTask;
        }

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
        {
            MessageActionsCalls++;
            return Task.CompletedTask;
        }

        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default)
        {
            SessionReceiverCalls++;
            return Task.CompletedTask;
        }

        public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default)
        {
            ProcessSessionArgsCalls++;
            return Task.CompletedTask;
        }
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
