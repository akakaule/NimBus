#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class ScopedDynamicHandlerRegistrationTests
{
    private const string EventTypeId = "scoped.dynamic.event.v1";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScopedDynamicRegistration_WithoutScopeFactory_FailsBeforeFactoryReceivesRootProvider(
        bool useFallback)
    {
        var services = new ServiceCollection();
        var builder = new NimBusSubscriberBuilder(services);
        var factoryCalls = 0;

        IEventJsonHandler CreateHandler(IServiceProvider _)
        {
            factoryCalls++;
            return new DelegateEventJsonHandler((_, _) => Task.CompletedTask);
        }

        if (useFallback)
            builder.AddScopedDynamicFallbackHandler(CreateHandler);
        else
            builder.AddScopedDynamicHandler(EventTypeId, CreateHandler);

        await using var root = services.BuildServiceProvider();
        var provider = new EventHandlerProvider();
        foreach (var registration in builder.HandlerRegistrations)
            registration.Register(root, provider);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            provider.Handle(MessageContextStub.ForEventType(EventTypeId, "{}")));

        StringAssert.Contains(exception.Message, nameof(IServiceScopeFactory), StringComparison.Ordinal);
        Assert.AreEqual(0, factoryCalls);
    }
}
