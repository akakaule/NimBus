#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The scheduler itself owns almost no logic: one tick must open a scope, resolve
/// the request-scoped <see cref="IHeartbeatService"/> from it, and never let a
/// failure escape and stop the loop for the lifetime of the process. The ordering
/// inside a tick is asserted in <see cref="HeartbeatServiceTests"/>, where the
/// store makes it observable.
/// </summary>
[TestClass]
public class HeartbeatBackgroundServiceTests
{
    [TestMethod]
    public async Task RunOnceAsync_resolves_the_service_from_a_scope_and_runs_one_tick()
    {
        var heartbeatService = new RecordingHeartbeatService();
        var scopeFactory = new RecordingScopeFactory(heartbeatService);
        var sut = CreateSut(scopeFactory);

        await sut.RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, scopeFactory.ScopesCreated, "The store is request-scoped, so each tick needs its own scope.");
        Assert.AreEqual(1, heartbeatService.TickCount);
        Assert.IsTrue(scopeFactory.LastScopeDisposed, "The scope must be disposed, or every tick leaks a store.");
    }

    [TestMethod]
    public async Task RunOnceAsync_swallows_a_failed_tick_so_the_schedule_survives()
    {
        var heartbeatService = new RecordingHeartbeatService
        {
            OnTick = () => throw new InvalidOperationException("store unavailable"),
        };
        var sut = CreateSut(new RecordingScopeFactory(heartbeatService));

        await sut.RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, heartbeatService.TickCount);
    }

    [TestMethod]
    public async Task RunOnceAsync_swallows_cancellation_on_shutdown()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var heartbeatService = new RecordingHeartbeatService
        {
            OnTick = () => throw new OperationCanceledException(),
        };
        var sut = CreateSut(new RecordingScopeFactory(heartbeatService));

        await sut.RunOnceAsync(stopping.Token);

        Assert.AreEqual(1, heartbeatService.TickCount);
    }

    private static HeartbeatBackgroundService CreateSut(IServiceScopeFactory scopeFactory)
        => new(scopeFactory, NullLogger<HeartbeatBackgroundService>.Instance);

    private sealed class RecordingHeartbeatService : IHeartbeatService
    {
        public int TickCount { get; private set; }

        public Action? OnTick { get; set; }

        public Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default)
        {
            TickCount++;
            OnTick?.Invoke();
            return Task.FromResult(true);
        }

        public Task<HeartbeatSettings> GetSettingsAsync() => throw new NotSupportedException();
        public Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings) => throw new NotSupportedException();
        public Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync() => throw new NotSupportedException();
        public Task<int> SweepTimeoutsAsync() => throw new NotSupportedException();
        public Task<int> SendHeartbeatsAsync(bool force = false) => throw new NotSupportedException();
        public Task SetEndpointEnabledAsync(string endpointId, bool enabled) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync() => throw new NotSupportedException();
        public Task<bool> ProbeResolverAsync() => throw new NotSupportedException();
    }

    private sealed class RecordingScopeFactory : IServiceScopeFactory
    {
        private readonly IHeartbeatService _heartbeatService;

        public RecordingScopeFactory(IHeartbeatService heartbeatService) => _heartbeatService = heartbeatService;

        public int ScopesCreated { get; private set; }

        public bool LastScopeDisposed => _lastScope?.Disposed == true;

        private RecordingScope? _lastScope;

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            _lastScope = new RecordingScope(_heartbeatService);
            return _lastScope;
        }

        private sealed class RecordingScope : IServiceScope, IServiceProvider
        {
            private readonly IHeartbeatService _heartbeatService;

            public RecordingScope(IHeartbeatService heartbeatService) => _heartbeatService = heartbeatService;

            public bool Disposed { get; private set; }

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
                => serviceType == typeof(IHeartbeatService) ? _heartbeatService : null;

            public void Dispose() => Disposed = true;
        }
    }
}
