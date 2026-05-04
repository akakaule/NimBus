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

    public Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(-1);
        lock (state.Gate)
        {
            return Task.FromResult(state.LastReplayedSequence);
        }
    }

    public Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            if (state.LastReplayedSequence != expectedCurrent)
                return Task.FromResult(false);
            // Forward-only invariant: never let the checkpoint go backwards.
            if (newValue <= state.LastReplayedSequence)
                return Task.FromResult(false);
            state.LastReplayedSequence = newValue;
            return Task.FromResult(true);
        }
    }

    public Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state))
            return Task.FromResult(0);
        lock (state.Gate)
        {
            return Task.FromResult(state.ActiveParkCount);
        }
    }

    /// <summary>
    /// Increments the active-park counter. Called by the in-memory parked-message
    /// store on a successful park to keep <see cref="GetActiveParkCount"/>
    /// cheap. Not part of the public <see cref="ISessionStateStore"/> surface —
    /// SQL/Cosmos providers update their counter via their own DB calls.
    /// </summary>
    internal void IncrementActiveParkCount(string endpointId, string sessionId)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            state.ActiveParkCount += 1;
        }
    }

    /// <summary>
    /// Decrements the active-park counter (clamped at zero). Called by the
    /// in-memory parked-message store on a successful replay/skip/dead-letter.
    /// </summary>
    internal void DecrementActiveParkCount(string endpointId, string sessionId)
    {
        if (!_states.TryGetValue((endpointId, sessionId), out var state)) return;
        lock (state.Gate)
        {
            if (state.ActiveParkCount > 0) state.ActiveParkCount -= 1;
        }
    }

    /// <summary>Reconciles the active-park counter against an authoritative count.</summary>
    internal void SetActiveParkCount(string endpointId, string sessionId, int count)
    {
        var state = GetOrCreate(endpointId, sessionId);
        lock (state.Gate)
        {
            state.ActiveParkCount = count < 0 ? 0 : count;
        }
    }

    private sealed class SessionStateRecord
    {
        public object Gate { get; } = new();
        public string? BlockedByEventId { get; set; }
        public int DeferredCount { get; set; }
        public int NextDeferralSequence { get; set; }
        public int LastReplayedSequence { get; set; } = -1;
        public int ActiveParkCount { get; set; }
    }
}
