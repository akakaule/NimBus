#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class InboxPurgeHostedServiceTests
{
    [TestMethod]
    public async Task Purge_failure_is_retried_on_the_next_tick()
    {
        var store = new ControllableInboxStore { FailFirstPurge = true };
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedUtcTimeProvider(now);
        var options = new InboxOptions
        {
            RetentionPeriod = TimeSpan.FromDays(7),
            CleanupInterval = TimeSpan.FromMilliseconds(5),
        };
        var sut = new InboxPurgeHostedService(
            store,
            "Billing",
            options,
            timeProvider,
            NullLogger<InboxPurgeHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await store.SecondPurge.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(2, store.PurgeCalls);
        Assert.AreEqual(now - options.RetentionPeriod, store.LastCutoff);
        Assert.AreEqual("Billing", store.LastEndpointId, "Cleanup must stay scoped to this subscriber's endpoint");
    }

    [TestMethod]
    public async Task Stop_cancels_an_in_progress_purge()
    {
        var store = new ControllableInboxStore { BlockUntilCancelled = true };
        var options = new InboxOptions
        {
            CleanupInterval = TimeSpan.FromMilliseconds(5),
        };
        var sut = new InboxPurgeHostedService(
            store,
            "Billing",
            options,
            TimeProvider.System,
            NullLogger<InboxPurgeHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await store.PurgeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(store.ObservedCancellation);
    }

    private sealed class ControllableInboxStore : IInboxStore
    {
        private readonly TaskCompletionSource _purgeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPurge =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailFirstPurge { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public int PurgeCalls { get; private set; }
        public string? LastEndpointId { get; private set; }
        public DateTimeOffset LastCutoff { get; private set; }
        public bool ObservedCancellation { get; private set; }
        public Task PurgeStarted => _purgeStarted.Task;
        public Task SecondPurge => _secondPurge.Task;

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default)
        {
            PurgeCalls++;
            LastEndpointId = endpointId;
            LastCutoff = olderThan;
            _purgeStarted.TrySetResult();

            if (FailFirstPurge && PurgeCalls == 1)
                throw new InvalidOperationException("provider details");

            if (PurgeCalls >= 2)
                _secondPurge.TrySetResult();

            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            return 0;
        }
    }

    private sealed class FixedUtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedUtcTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
