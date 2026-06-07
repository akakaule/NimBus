#pragma warning disable CA1707, CA2007
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

[TestClass]
public class FallbackHandlerTests
{
    private sealed class RecordingHandler : IEventJsonHandler
    {
        public int Calls;
        public Task Handle(IMessageContext context, CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task Unregistered_eventTypeId_routes_to_fallback()
    {
        var provider = new EventHandlerProvider();
        var fallback = new RecordingHandler();
        provider.RegisterFallbackHandler(() => fallback);

        await provider.Handle(MessageContextStub.ForEventType("some.unmapped.type.v1", "{}"));

        Assert.AreEqual(1, fallback.Calls, "An EventTypeId with no specific handler must route to the fallback");
    }

    [TestMethod]
    public async Task Specific_handler_wins_over_fallback()
    {
        var provider = new EventHandlerProvider();
        var specific = new RecordingHandler();
        var fallback = new RecordingHandler();
        provider.RegisterHandler("known.type.v1", () => specific);
        provider.RegisterFallbackHandler(() => fallback);

        await provider.Handle(MessageContextStub.ForEventType("known.type.v1", "{}"));

        Assert.AreEqual(1, specific.Calls);
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task No_handler_and_no_fallback_still_throws()
    {
        var provider = new EventHandlerProvider();
        await Assert.ThrowsExceptionAsync<NimBus.Core.Messages.Exceptions.EventHandlerNotFoundException>(
            () => provider.Handle(MessageContextStub.ForEventType("nothing.v1", "{}")));
    }
}
