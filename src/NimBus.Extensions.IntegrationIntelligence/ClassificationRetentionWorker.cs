using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Retries removal of classifications whose source event has been purged.</summary>
internal sealed class ClassificationRetentionWorker(IServiceScopeFactory scopes, ILogger<ClassificationRetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RetryLog = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, "RetentionRetry"), "Classification retention is unavailable; cleanup will retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IFailureClassificationStore>() as IClassificationRetentionStore;
                var messages = scope.ServiceProvider.GetRequiredService<IMessageTrackingStore>();
                if (store is not null)
                {
                    await ReconcileAsync(store, messages, logger, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { RetryLog(logger, null); }
            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal static async Task ReconcileAsync(IClassificationRetentionStore store, IMessageTrackingStore messages, ILogger logger, CancellationToken cancellationToken)
    {
        await foreach (var candidate in store.GetRetentionCandidatesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var deleted = false;
                try { deleted = await messages.GetEvent(candidate.EndpointId, candidate.EventId).WaitAsync(cancellationToken).ConfigureAwait(false) is null; }
                catch (EndpointNotFoundException) { deleted = true; }
                if (deleted) await store.DeleteFailureAsync(candidate.FailureMessageId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { RetryLog(logger, null); }
        }
    }
}
