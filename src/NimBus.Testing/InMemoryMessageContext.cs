using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

public class InMemoryMessageContext : IMessageContext
{
    private readonly IMessage _message;
    private readonly InMemorySessionState _sessionState;
    private readonly ISessionStateStore _sessionStateStore;

    public InMemoryMessageContext(IMessage message, InMemorySessionState sessionState)
        : this(message, sessionState, sessionStateStore: null)
    {
    }

    public InMemoryMessageContext(IMessage message, InMemorySessionState sessionState, ISessionStateStore sessionStateStore)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _sessionStateStore = sessionStateStore;
        EnqueuedTimeUtc = DateTime.UtcNow;
    }

    // IMessage properties - delegate to wrapped message
    public string EventId => _message.EventId;
    public string To => _message.To;
    public string SessionId => _message.SessionId;
    public string CorrelationId => _message.CorrelationId;
    public string MessageId => _message.MessageId;
    public MessageType MessageType => _message.MessageType;
    public MessageContent MessageContent => _message.MessageContent;
    public string ParentMessageId => _message.ParentMessageId;
    public string OriginatingMessageId => _message.OriginatingMessageId;
    public int? RetryCount => _message.RetryCount;
    public string From => _message.From;
    public string OriginatingFrom => _message.OriginatingFrom;
    public string EventTypeId => _message.EventTypeId;
    public string OriginalSessionId => _message.OriginalSessionId;
    public int? DeferralSequence => _message.DeferralSequence;

    // IReceivedMessage
    public DateTime EnqueuedTimeUtc { get; }
    public string DeadLetterReason => DeadLetterReasonRecorded;
    public string DeadLetterErrorDescription => DeadLetterErrorDescriptionRecorded;

    // Observable state for test assertions
    public bool IsCompleted { get; private set; }
    public bool IsAbandoned { get; private set; }
    public bool IsDeadLettered { get; private set; }
    public string DeadLetterReasonRecorded { get; private set; }
    public string DeadLetterErrorDescriptionRecorded { get; private set; }

    // IMessageContext
    public bool IsDeferred => _message.DeferralSequence.HasValue;
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public DateTime? HandlerStartedAtUtc { get; set; }

    public Task Complete(CancellationToken cancellationToken = default)
    {
        IsCompleted = true;
        return Task.CompletedTask;
    }

    public Task Abandon(TransientException exception)
    {
        IsAbandoned = true;
        return Task.CompletedTask;
    }

    public Task DeadLetter(string reason, Exception exception = null, CancellationToken cancellationToken = default)
    {
        IsDeadLettered = true;
        DeadLetterReasonRecorded = reason;
        DeadLetterErrorDescriptionRecorded = exception?.Message;
        return Task.CompletedTask;
    }

    public Task Defer(CancellationToken cancellationToken = default)
    {
        _sessionState.DeferredMessages.Add(_message);
        _sessionState.DeferredCount++;
        return Task.CompletedTask;
    }

    public Task DeferOnly(CancellationToken cancellationToken = default)
    {
        _sessionState.DeferredMessages.Add(_message);
        return Task.CompletedTask;
    }

    public Task<IMessageContext> ReceiveNextDeferred(CancellationToken cancellationToken = default)
    {
        if (_sessionState.DeferredMessages.Count == 0)
            return Task.FromResult<IMessageContext>(null);

        var next = _sessionState.DeferredMessages[0];
        _sessionState.DeferredMessages.RemoveAt(0);
        _sessionState.DeferredCount = Math.Max(0, _sessionState.DeferredCount - 1);
        return Task.FromResult<IMessageContext>(new InMemoryMessageContext(next, _sessionState, _sessionStateStore));
    }

    public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken cancellationToken = default)
    {
        return ReceiveNextDeferred(cancellationToken);
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task BlockSession(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.BlockSession(To, SessionId, EventId, cancellationToken);
        _sessionState.BlockedByEventId = EventId;
        return Task.CompletedTask;
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task UnblockSession(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.UnblockSession(To, SessionId, cancellationToken);
        _sessionState.BlockedByEventId = null;
        return Task.CompletedTask;
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.IsSessionBlocked(To, SessionId, cancellationToken);
        return Task.FromResult(!string.IsNullOrEmpty(_sessionState.BlockedByEventId));
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.IsSessionBlockedByThis(To, SessionId, EventId, cancellationToken);
        return Task.FromResult(_sessionState.BlockedByEventId == EventId);
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.IsSessionBlockedByEventId(To, SessionId, cancellationToken);
        return Task.FromResult(!string.IsNullOrEmpty(_sessionState.BlockedByEventId));
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public async Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
        {
            var blockedBy = await _sessionStateStore.GetBlockedByEventId(To, SessionId, cancellationToken);
            return string.IsNullOrEmpty(blockedBy) ? null : blockedBy;
        }
        return _sessionState.BlockedByEventId;
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.GetNextDeferralSequenceAndIncrement(To, SessionId, cancellationToken);
        return Task.FromResult(_sessionState.NextDeferralSequence++);
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task IncrementDeferredCount(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.IncrementDeferredCount(To, SessionId, cancellationToken);
        _sessionState.DeferredCount++;
        return Task.CompletedTask;
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task DecrementDeferredCount(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.DecrementDeferredCount(To, SessionId, cancellationToken);
        _sessionState.DeferredCount = Math.Max(0, _sessionState.DeferredCount - 1);
        return Task.CompletedTask;
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<int> GetDeferredCount(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.GetDeferredCount(To, SessionId, cancellationToken);
        return Task.FromResult(_sessionState.DeferredCount);
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.HasDeferredMessages(To, SessionId, cancellationToken);
        return Task.FromResult(_sessionState.DeferredCount > 0 || _sessionState.DeferredMessages.Count > 0);
    }

    [Obsolete("Use ISessionStateStore via DI. Will be removed in v2.")]
    public Task ResetDeferredCount(CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore != null)
            return _sessionStateStore.ResetDeferredCount(To, SessionId, cancellationToken);
        _sessionState.DeferredCount = 0;
        return Task.CompletedTask;
    }

}
