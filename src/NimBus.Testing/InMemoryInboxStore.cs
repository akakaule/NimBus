using System.Collections.Concurrent;
using NimBus.Core.Inbox;

namespace NimBus.Testing;

/// <summary>
/// Thread-safe in-memory inbox store intended for tests and local development.
/// Records are keyed by the (endpoint, message) pair so endpoints sharing one store
/// never observe each other's fan-out deliveries.
/// </summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private const int DefaultPurgeBatchSize = 1_000;
    private readonly ConcurrentDictionary<(string EndpointId, string MessageId), DateTimeOffset> _processed = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _purgeBatchSize;

    /// <summary>
    /// Initializes a new instance using the system clock and the default bounded purge size.
    /// </summary>
    public InMemoryInboxStore()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance using a caller-provided clock.
    /// </summary>
    /// <param name="timeProvider">Clock used to timestamp the first record of each message.</param>
    /// <param name="purgeBatchSize">Maximum number of records removed by one purge call.</param>
    public InMemoryInboxStore(TimeProvider timeProvider, int purgeBatchSize = DefaultPurgeBatchSize)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (purgeBatchSize is < 1 or > DefaultPurgeBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(purgeBatchSize),
                purgeBatchSize,
                $"The purge batch size must be between 1 and {DefaultPurgeBatchSize}.");
        }

        _timeProvider = timeProvider;
        _purgeBatchSize = purgeBatchSize;
    }

    /// <inheritdoc />
    public Task<bool> HasProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        return Task.FromResult(_processed.ContainsKey((endpointId, messageId)));
    }

    /// <inheritdoc />
    public Task RecordProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        _processed.TryAdd((endpointId, messageId), _timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> PurgeExpiredAsync(
        string endpointId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        var removed = 0;
        foreach (var entry in _processed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.Key.EndpointId, endpointId, StringComparison.Ordinal)
                && entry.Value < olderThan
                && _processed.TryRemove(entry))
            {
                removed++;
                if (removed >= _purgeBatchSize)
                {
                    break;
                }
            }
        }

        return Task.FromResult(removed);
    }

    private static void ValidateIdentity(string endpointId, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
    }
}
