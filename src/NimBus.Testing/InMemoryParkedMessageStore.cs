using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// In-memory implementation of <see cref="IParkedMessageStore"/> for unit tests
/// and the in-memory message-store registration. Holds parked rows in a thread-
/// safe dictionary keyed by <c>(EndpointId, MessageId)</c>; idempotency is the
/// natural-key invariant. Sequence allocation goes through the supplied
/// <see cref="InMemorySessionStateStore"/> so that tests can interleave parks
/// across both stores under realistic ordering semantics.
/// </summary>
public sealed class InMemoryParkedMessageStore : IParkedMessageStore
{
    private readonly InMemorySessionStateStore _sessionStateStore;
    private readonly ConcurrentDictionary<(string EndpointId, string MessageId), ParkedMessage> _byMessageId = new();
    private readonly object _gate = new();

    public InMemoryParkedMessageStore(InMemorySessionStateStore sessionStateStore)
    {
        _sessionStateStore = sessionStateStore ?? throw new ArgumentNullException(nameof(sessionStateStore));
    }

    public Task<long> ParkAsync(ParkedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var key = (message.EndpointId, message.MessageId);
            if (_byMessageId.TryGetValue(key, out var existing))
            {
                // Idempotent: re-parking the same message returns the existing sequence.
                return Task.FromResult(existing.ParkSequence);
            }

            // Allocate the sequence from the session-state store so the in-memory
            // counter stays consistent with what GetNextDeferralSequenceAndIncrement
            // returned for live SDK callers.
            var sequence = _sessionStateStore
                .GetNextDeferralSequenceAndIncrement(message.EndpointId, message.SessionKey, cancellationToken)
                .GetAwaiter().GetResult();

            message.ParkSequence = sequence;
            if (message.ParkedAtUtc == default) message.ParkedAtUtc = DateTime.UtcNow;
            _byMessageId[key] = message;
            _sessionStateStore.IncrementActiveParkCount(message.EndpointId, message.SessionKey);
            return Task.FromResult((long)sequence);
        }
    }

    public Task<IReadOnlyList<ParkedMessage>> GetActiveAsync(string endpointId, string sessionKey, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ParkedMessage> rows;
        lock (_gate)
        {
            rows = _byMessageId.Values
                .Where(p =>
                    p.EndpointId == endpointId &&
                    p.SessionKey == sessionKey &&
                    p.ParkSequence > afterSequence &&
                    p.ReplayedAtUtc is null &&
                    p.SkippedAtUtc is null &&
                    p.DeadLetteredAtUtc is null)
                .OrderBy(p => p.ParkSequence)
                .Take(limit > 0 ? limit : int.MaxValue)
                .ToList();
        }
        return Task.FromResult(rows);
    }

    public Task MarkReplayedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue((endpointId, messageId), out var row)) return Task.CompletedTask;
            // Idempotent: leave the first replayed-at timestamp in place.
            if (row.ReplayedAtUtc is null && row.SkippedAtUtc is null && row.DeadLetteredAtUtc is null)
            {
                row.ReplayedAtUtc = DateTime.UtcNow;
                _sessionStateStore.DecrementActiveParkCount(endpointId, row.SessionKey);
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkSkippedAsync(string endpointId, string sessionKey, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        lock (_gate)
        {
            foreach (var messageId in messageIds)
            {
                if (!_byMessageId.TryGetValue((endpointId, messageId), out var row)) continue;
                if (row.ReplayedAtUtc is null && row.SkippedAtUtc is null && row.DeadLetteredAtUtc is null)
                {
                    row.SkippedAtUtc = DateTime.UtcNow;
                    _sessionStateStore.DecrementActiveParkCount(endpointId, row.SessionKey);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<int> IncrementReplayAttemptAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue((endpointId, messageId), out var row))
                return Task.FromResult(0);
            row.ReplayAttemptCount += 1;
            return Task.FromResult(row.ReplayAttemptCount);
        }
    }

    public Task MarkDeadLetteredAsync(string endpointId, string messageId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue((endpointId, messageId), out var row)) return Task.CompletedTask;
            if (row.ReplayedAtUtc is null && row.SkippedAtUtc is null && row.DeadLetteredAtUtc is null)
            {
                row.DeadLetteredAtUtc = DateTime.UtcNow;
                row.DeadLetterReason = reason;
                _sessionStateStore.DecrementActiveParkCount(endpointId, row.SessionKey);
            }
        }
        return Task.CompletedTask;
    }

    public Task<int> CountActiveAsync(string endpointId, string sessionKey, CancellationToken cancellationToken = default)
    {
        int count;
        lock (_gate)
        {
            count = _byMessageId.Values.Count(p =>
                p.EndpointId == endpointId &&
                p.SessionKey == sessionKey &&
                p.ReplayedAtUtc is null &&
                p.SkippedAtUtc is null &&
                p.DeadLetteredAtUtc is null);
        }
        return Task.FromResult(count);
    }
}
