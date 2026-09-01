#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class CircuitBreakerLifecycleHostedServiceTests
{
    [TestMethod]
    public async Task Transition_is_dispatched_once_to_lifecycle_observers()
    {
        var breaker = new EndpointCircuitBreaker(
            "billing",
            new CircuitBreakerOptions { MinimumThroughput = 1, FailurePercentageThreshold = 100 });
        var observer = new RecordingCircuitObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        using var service = new CircuitBreakerLifecycleHostedService(
            breaker,
            notifier,
            NullLogger<CircuitBreakerLifecycleHostedService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(cts.Token);
        breaker.RecordFailure(new TransientException("down"));
        var change = await observer.Observed.Task.WaitAsync(cts.Token);
        await service.StopAsync(cts.Token);

        Assert.AreEqual("billing", change.Endpoint);
        Assert.AreEqual(CircuitState.Open, change.To);
        Assert.AreEqual(1, observer.Calls);
    }

    private sealed class RecordingCircuitObserver : IMessageLifecycleObserver
    {
        public TaskCompletionSource<CircuitStateChangeContext> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task OnCircuitStateChanged(
            CircuitStateChangeContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Observed.TrySetResult(context);
            return Task.CompletedTask;
        }
    }
}
