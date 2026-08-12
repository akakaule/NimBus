using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Drives the heartbeat schedule: every 30 seconds it opens a scope and runs one
/// tick. All the decisions live in <see cref="IHeartbeatService.RunScheduledTickAsync"/> —
/// this class exists only to give a request-scoped service a clock.
/// </summary>
public sealed partial class HeartbeatBackgroundService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatBackgroundService> _logger;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="scopeFactory">Opens the per-tick scope the request-scoped message store needs.</param>
    /// <param name="logger">Diagnostics.</param>
    public HeartbeatBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HeartbeatBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs one tick in its own scope. A failed tick is logged and swallowed: the
    /// next one is 30 seconds away, and letting it escape would stop the scheduler
    /// for the lifetime of the process.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var heartbeatService = scope.ServiceProvider.GetRequiredService<IHeartbeatService>();
            await heartbeatService.RunScheduledTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogScheduledRunFailed(exception);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Scheduled heartbeat run failed.")]
    private partial void LogScheduledRunFailed(Exception exception);
}
