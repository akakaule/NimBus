using System;
using NimBus.Core.Messages;

namespace NimBus.SDK.EventHandlers
{
    public interface IEventHandlerContext
    {
        /// <summary>
        /// The Id of the currently processed message.
        /// </summary>
        string MessageId { get; }

        string EventId { get; }

        string EventType { get; }

        string CorrelationId { get; }

        /// <summary>
        /// The outcome signalled by the handler. Defaults to
        /// <see cref="HandlerOutcome.Default"/>; flips to
        /// <see cref="HandlerOutcome.PendingHandoff"/> after
        /// <see cref="MarkPendingHandoff"/> is called.
        /// </summary>
        HandlerOutcome Outcome { get; }

        /// <summary>
        /// Metadata supplied by the handler via <see cref="MarkPendingHandoff"/>.
        /// Null when <see cref="Outcome"/> is <see cref="HandlerOutcome.Default"/>.
        /// </summary>
        HandoffMetadata HandoffMetadata { get; }

        /// <summary>
        /// Signals that the handler has handed work off to a long-running
        /// external system. The subscriber will send a PendingHandoffResponse
        /// to the Resolver and block the session until the Manager settles
        /// the message via CompleteHandoff or FailHandoff. Idempotent — if
        /// called multiple times, the last call wins.
        /// </summary>
        /// <param name="reason">Free-text reason describing why the handler is handing off (required).</param>
        /// <param name="externalJobId">Optional external-system identifier (e.g. a DMF job id).</param>
        /// <param name="expectedBy">Optional duration after which the work is expected to settle.</param>
        void MarkPendingHandoff(string reason, string externalJobId = null, TimeSpan? expectedBy = null);
    }

    public class EventHandlerContext : IEventHandlerContext
    {
        private readonly IMessageContext _messageContext;

        public EventHandlerContext()
        {
        }

        public EventHandlerContext(IMessageContext messageContext)
        {
            _messageContext = messageContext;
        }

        /// <summary>
        /// The Id of the currently processed message.
        /// </summary>
        public string MessageId { get; set; }

        public string EventId { get; set; }

        public string EventType { get; set; }

        public string CorrelationId { get; set; }

        public HandlerOutcome Outcome { get; private set; }

        public HandoffMetadata HandoffMetadata { get; private set; }

        public void MarkPendingHandoff(string reason, string externalJobId = null, TimeSpan? expectedBy = null)
        {
            Outcome = HandlerOutcome.PendingHandoff;
            HandoffMetadata = new HandoffMetadata(reason, externalJobId, expectedBy);

            if (_messageContext != null)
            {
                _messageContext.HandlerOutcome = HandlerOutcome.PendingHandoff;
                _messageContext.HandoffMetadata = HandoffMetadata;
            }
        }
    }
}
