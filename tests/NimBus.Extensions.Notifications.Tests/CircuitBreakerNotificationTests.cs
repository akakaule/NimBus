#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public sealed class CircuitBreakerNotificationTests
{
    [TestMethod]
    public async Task Open_is_critical_and_close_is_information()
    {
        var channel = new FakeNotificationChannel();
        var observer = new NotificationLifecycleObserver(
            [channel],
            new NotificationOptions { NotifyOnCircuitOpen = true });
        var timestamp = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await observer.OnCircuitStateChanged(new CircuitStateChangeContext(
            "billing", CircuitState.Closed, CircuitState.Open, "threshold", timestamp));
        await observer.OnCircuitStateChanged(new CircuitStateChangeContext(
            "billing", CircuitState.HalfOpen, CircuitState.Closed, "recovered", timestamp));

        Assert.AreEqual(2, channel.Received.Count);
        Assert.AreEqual(NotificationSeverity.Critical, channel.Received[0].Severity);
        Assert.AreEqual("circuit:billing:Open", channel.Received[0].EventId);
        Assert.AreEqual(NotificationSeverity.Information, channel.Received[1].Severity);
        Assert.AreEqual("circuit:billing:Closed", channel.Received[1].EventId);
    }

    [TestMethod]
    public async Task Disabled_option_suppresses_circuit_notifications()
    {
        var channel = new FakeNotificationChannel();
        var observer = new NotificationLifecycleObserver(
            [channel],
            new NotificationOptions { NotifyOnCircuitOpen = false });

        await observer.OnCircuitStateChanged(new CircuitStateChangeContext(
            "billing", CircuitState.Closed, CircuitState.Open, "threshold", DateTimeOffset.UtcNow));

        Assert.AreEqual(0, channel.Received.Count);
    }
}

