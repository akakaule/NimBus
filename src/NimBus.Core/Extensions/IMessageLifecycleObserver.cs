using NimBus.Core.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Observes message lifecycle events without modifying the message flow.
    /// All methods have default no-op implementations so observers only need to override events they care about.
    /// </summary>
    public interface IMessageLifecycleObserver
    {
        /// <summary>
        /// Called when a message is received and about to be processed.
        /// </summary>
        Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Called when a message has been successfully processed.
        /// </summary>
        Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Called when a message handler throws an exception.
        /// </summary>
        Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Called when a message is sent to the dead-letter queue.
        /// </summary>
        Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception exception = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Context information for message lifecycle events.
    /// </summary>
    public class MessageLifecycleContext
    {
        public string MessageId { get; init; }
        public string EventId { get; init; }
        public string EventTypeId { get; init; }
        public string CorrelationId { get; init; }
        public string SessionId { get; init; }
        public MessageType MessageType { get; init; }
        public DateTimeOffset EnqueuedTimeUtc { get; init; }
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Creates a lifecycle context from a message context.
        /// </summary>
        public static MessageLifecycleContext FromMessageContext(IMessageContext messageContext)
        {
            return new MessageLifecycleContext
            {
                MessageId = messageContext.MessageId,
                EventId = messageContext.EventId,
                EventTypeId = messageContext.EventTypeId,
                CorrelationId = messageContext.CorrelationId,
                SessionId = messageContext.SessionId,
                MessageType = messageContext.MessageType,
                EnqueuedTimeUtc = messageContext.EnqueuedTimeUtc,
            };
        }
    }
}
