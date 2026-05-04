using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// In-memory implementation of <see cref="ISessionStateStore"/>. Holds session
/// state in a thread-safe dictionary keyed by <c>(endpointId, sessionId)</c>.
/// Intended for unit tests and the in-memory message-store registration path —
/// behavior matches the SQL Server / Cosmos providers but with no persistence.
/// </summary>
public sealed class InMemorySessionStateStore : ISessionStateStore
{
    private readonly ConcurrentDictionary<(string EndpointId, string SessionId), SessionStateRecord> _states = new();

    private SessionStateRecord GetOrCreate(string endpointId, string sessionId)
        => _states.GetOrAdd((endpointId, sessionId), _ => new SessionStateRecord());

    public Task BlockSession(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            state.BlockedByEventId = eventId;
        }
        return Task.CompletedTask;
    }

    public Task UnblockSession(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (_states.TryGetValue((endpointId, sessionId), out var state))
        {
            lock (state.Gate)
            {
                state.BlockedByEventId = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsSessionBlocked(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(false);
        lock (state.Gate)
        {
            return Task.FromResult(!string.IsNullOrEmpty(state.BlockedByEventId) || state.DeferredCount > 0);
        }
    }

    public Task<bool> IsSessionBlockedByThis(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(false);
        lock (state.Gate)
        {
            return Task.FromResult(
                !string.IsNullOrEmpty(state.BlockedByEventId)
                && string.Equals(state.BlockedByEventId, eventId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task<bool> IsSessionBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(false);
        lock (state.Gate)
        {
            return Task.FromResult(!string.IsNullOrEmpty(state.BlockedByEventId));
        }
    }

    public Task<string> GetBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(string.Empty);
        lock (state.Gate)
        {
            return Task.FromResult(state.BlockedByEventId ?? string.Empty);
        }
    }

    public Task<int> GetNextDeferralSequenceAndIncrement(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            var sequence = state.NextDeferralSequence;
            state.NextDeferralSequence = sequence + 1;
            return Task.FromResult(sequence);
        }
    }

    public Task IncrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            state.DeferredCount += 1;
        }
        return Task.CompletedTask;
    }

    public Task DecrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.CompletedTask;
        lock (state.Gate)
        {
            if (state.DeferredCount > 0)
                state.DeferredCount -= 1;
        }
        return Task.CompletedTask;
    }

    public Task<int> GetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(0);
        lock (state.Gate)
        {
            return Task.FromResult(state.DeferredCount);
        }
    }

    public Task<bool> HasDeferredMessages(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(false);
        lock (state.Gate)
        {
            return Task.FromResult(state.DeferredCount > 0);
        }
    }

    public Task ResetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.CompletedTask;
        lock (state.Gate)
        {
            state.DeferredCount = 0;
        }
        return Task.CompletedTask;
    }

    // TODO: task #5 implementation — issue #20
    public Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    // TODO: task #5 implementation — issue #20
    public Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // TODO: task #5 implementation — issue #20
    public Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    private sealed class SessionStateRecord
    {
        public object Gate { get; } = new();
        public string? BlockedByEventId { get; set; }
        public int DeferredCount { get; set; }
        public int NextDeferralSequence { get; set; }
    }
}
