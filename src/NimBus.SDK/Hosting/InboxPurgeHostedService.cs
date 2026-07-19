using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Inbox;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Periodically removes inbox records that are older than the configured retention period.
/// </summary>
public sealed class InboxPurgeHostedService : BackgroundService
{
    private const string PurgeOperation = "purge";
    private readonly IInboxStore _inboxStore;
    private readonly InboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InboxPurgeHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxPurgeHostedService"/> class.
    /// </summary>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="options">The inbox retention and cleanup options.</param>
    /// <param name="timeProvider">The clock and timer provider.</param>
    /// <param name="logger">The optional structured logger.</param>
    public InboxPurgeHostedService(
        IInboxStore inboxStore,
        InboxOptions options,
        TimeProvider timeProvider,
        ILogger<InboxPurgeHostedService>? logger = null)
    {
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<InboxPurgeHostedService>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CleanupInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var cutoff = _timeProvider.GetUtcNow() - _options.RetentionPeriod;
                    var purged = await _inboxStore.PurgeExpiredAsync(cutoff, stoppingToken);
                    _logger.LogInformation(
                        "Inbox cleanup purged {PurgedRecordCount} expired records.",
                        purged);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    NimBusMeters.InboxOperationFailed.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            MessagingAttributes.NimBusStoreOperation,
                            PurgeOperation));
                    _logger.LogWarning(
                        "Inbox cleanup failed with {ExceptionType}; cleanup will retry on the next tick.",
                        exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cooperative host shutdown.
        }
    }
}
