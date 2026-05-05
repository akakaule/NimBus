#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Testing;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ.Extensions;
using NimBus.Transport.RabbitMQ.Topology;

namespace NimBus.Transport.RabbitMQ.Tests;

[TestClass]
public class RabbitMqSessionOpsTests
{
    [TestMethod]
    public void AddRabbitMqTransport_RegistersITransportSessionOps()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
        });

        var sp = services.BuildServiceProvider();
        var sessionOps = sp.GetRequiredService<ITransportSessionOps>();

        Assert.IsInstanceOfType(sessionOps, typeof(RabbitMqSessionOps));
    }

    [TestMethod]
    public async Task PreviewSessionAsync_ThrowsNotSupportedWithRemediation()
    {
        var sut = new RabbitMqSessionOps();
        var ex = await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => sut.PreviewSessionAsync("endpoint", "session-1", CancellationToken.None));
        StringAssert.Contains(ex.Message, "RabbitMqReceiverHostedService");
        StringAssert.Contains(ex.Message, "1D");
    }

    [TestMethod]
    public async Task PurgeSessionAsync_ThrowsNotSupported()
    {
        var sut = new RabbitMqSessionOps();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => sut.PurgeSessionAsync("endpoint", "session-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task PreviewSubscriptionAsync_ThrowsNotSupported()
    {
        var sut = new RabbitMqSessionOps();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => sut.PreviewSubscriptionAsync(
                "endpoint", "subscription",
                new[] { TransportMessageState.Active },
                enqueuedBeforeUtc: null,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task PurgeSubscriptionAsync_ThrowsNotSupported()
    {
        var sut = new RabbitMqSessionOps();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => sut.PurgeSubscriptionAsync(
                "endpoint", "subscription",
                new[] { TransportMessageState.Deferred },
                enqueuedBeforeUtc: null,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReprocessDeferredAsync_ThrowsNotSupported()
    {
        var sut = new RabbitMqSessionOps();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => sut.ReprocessDeferredAsync("endpoint", "session-1", CancellationToken.None));
    }

    [TestMethod]
    public void RabbitMqSessionOps_ImplementsITransportSessionOps()
    {
        Assert.IsTrue(typeof(ITransportSessionOps).IsAssignableFrom(typeof(RabbitMqSessionOps)));
    }

    [TestMethod]
    public void TransportMessageState_HasActiveAndDeferredOnly()
    {
        var values = Enum.GetValues<TransportMessageState>();
        CollectionAssert.AreEquivalent(
            new[] { TransportMessageState.Active, TransportMessageState.Deferred },
            values);
    }
}
