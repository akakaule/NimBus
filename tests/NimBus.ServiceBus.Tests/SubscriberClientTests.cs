#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
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
        AssertCtorThrowsArgumentNull(null, new EventHandlerProvider());
    }

    [TestMethod]
    public void Constructor_NullEventHandlerProvider_Throws()
    {
        AssertCtorThrowsArgumentNull(new RecordingMessageHandler(), null);
    }

    private static void AssertCtorThrowsArgumentNull(
        IMessageHandler? messageHandler,
        EventHandlerProvider? eventHandlerProvider)
    {
        var ctor = typeof(SubscriberClient).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        try
        {
            ctor.Invoke(new object?[] { messageHandler, eventHandlerProvider });
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
        EventHandlerProvider? eventHandlerProvider = null)
    {
        var ctorType = typeof(SubscriberClient);
        var ctor = ctorType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (SubscriberClient)ctor.Invoke(new object?[]
        {
            messageHandler ?? new RecordingMessageHandler(),
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

    private sealed class FakeTestEventHandler : IEventHandler<TestEvent>
    {
        public int HandleCalls { get; private set; }

        public Task Handle(TestEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }
}
