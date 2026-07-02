#pragma warning disable CA1707, CA2007, CS8618, CS8625, CS8603
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.MappingExecutor.Tests;

/// <summary>
/// Minimal <see cref="IMessageContext"/> stub for MappingExecutor unit tests.
/// Populates <c>MessageContent.EventContent.EventTypeId</c>, <c>EventJson</c>, and <c>SessionId</c>.
/// </summary>
public static class MessageContextStub
{
    public static IMessageContext ForEventType(string eventTypeId, string eventJson, string? sessionId = null)
        => new StubMessageContext(eventTypeId, eventJson, sessionId ?? $"session-{eventTypeId}");

    private sealed class StubMessageContext : IMessageContext
    {
        public StubMessageContext(string eventTypeId, string eventJson, string sessionId)
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson,
                }
            };
            EventTypeId = eventTypeId;
            SessionId = sessionId;
        }

        public MessageContent MessageContent { get; }
        public string EventTypeId { get; }
        public string SessionId { get; }

        // Minimal stubs — unused by the executor path
        public string EventId => string.Empty;
        public string To => string.Empty;
        public string CorrelationId => string.Empty;
        public string MessageId => string.Empty;
        public MessageType MessageType => MessageType.EventRequest;
        public string ParentMessageId => string.Empty;
        public string OriginatingMessageId => string.Empty;
        public int? RetryCount => null;
        public string From => string.Empty;
        public string OriginatingFrom => string.Empty;
        public string OriginalSessionId => string.Empty;
        public int? DeferralSequence => null;
        public DateTime EnqueuedTimeUtc => DateTime.UtcNow;
        public string DeadLetterReason => null;
        public string DeadLetterErrorDescription => null;
        public string HandoffReason => null;
        public string ExternalJobId => null;
        public DateTime? ExpectedBy => null;
        public bool IsDeferred => false;
        public int ThrottleRetryCount => 0;
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata HandoffMetadata { get; set; }
        public ActivityContext ParentTraceContext { get; set; }

        public Task Complete(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Abandon(TransientException exception) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Defer(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeferOnly(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IMessageContext> ReceiveNextDeferred(CancellationToken cancellationToken = default) => Task.FromResult<IMessageContext>(null);
        public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken cancellationToken = default) => Task.FromResult<IMessageContext>(null);
        public Task BlockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
