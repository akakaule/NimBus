using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Dispatches pending outbox messages to the real message sender.
    /// This is the core polling logic; wrap in a hosted service for background execution.
    /// </summary>
    public class OutboxDispatcher
    {
        private readonly IOutbox _outbox;
        private readonly ISender _sender;
        private readonly ILogger<OutboxDispatcher> _logger;

        public OutboxDispatcher(IOutbox outbox, ISender sender, ILogger<OutboxDispatcher> logger = null)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _logger = logger ?? NullLogger<OutboxDispatcher>.Instance;
        }

        /// <summary>
        /// Dispatches a batch of pending outbox messages.
        /// </summary>
        /// <param name="batchSize">Maximum number of messages to dispatch in one batch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of messages dispatched.</returns>
        public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
            if (pending.Count == 0)
                return 0;

            _logger.LogDebug("Outbox dispatch poll found {PendingCount} pending message(s)", pending.Count);

            var dispatched = new List<string>();
            foreach (var outboxMessage in pending)
            {
                if (!await DispatchOneAsync(outboxMessage, cancellationToken))
                {
                    // Stop dispatching on first failure to maintain ordering.
                    // The failed message will be retried on the next poll.
                    break;
                }
                dispatched.Add(outboxMessage.Id);
            }

            if (dispatched.Count > 0)
            {
                await _outbox.MarkAsDispatchedAsync(dispatched, cancellationToken);
            }

            return dispatched.Count;
        }

        private async Task<bool> DispatchOneAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
        {
            // Detach from the polling-loop activity so the dispatch span is a root that
            // links (rather than nests under) the original publish context.
            var savedCurrent = Activity.Current;
            Activity.Current = null;

            var startTimestamp = Stopwatch.GetTimestamp();
            Message message = null;
            string endpoint = null;
            Activity activity = null;

            try
            {
                ActivityLink? link = TryBuildLink(outboxMessage.TraceParent, outboxMessage.TraceState);
                activity = NimBusActivitySources.Outbox.StartActivity(
                    "NimBus.Outbox.Dispatch",
                    ActivityKind.Producer,
                    parentContext: default,
                    tags: null,
                    links: link.HasValue ? new[] { link.Value } : null);

                message = JsonConvert.DeserializeObject<Message>(outboxMessage.Payload, Constants.SafeJsonSettings);
                endpoint = message?.To;

                if (activity is not null)
                {
                    if (!link.HasValue)
                        activity.AddEvent(new ActivityEvent("nimbus.outbox.orphan_row"));
                    if (!string.IsNullOrEmpty(endpoint))
                        activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
                    if (!string.IsNullOrEmpty(outboxMessage.EventTypeId))
                        activity.SetTag(MessagingAttributes.NimBusEventType, outboxMessage.EventTypeId);
                    if (!string.IsNullOrEmpty(outboxMessage.MessageId))
                        activity.SetTag(MessagingAttributes.MessageId, outboxMessage.MessageId);
                }

                if (outboxMessage.ScheduledEnqueueTimeUtc.HasValue)
                {
                    await _sender.ScheduleMessage(message, new DateTimeOffset(outboxMessage.ScheduledEnqueueTimeUtc.Value, TimeSpan.Zero), cancellationToken);
                }
                else
                {
                    await _sender.Send(message, outboxMessage.EnqueueDelayMinutes, cancellationToken);
                }

                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = BuildDispatchTags(endpoint, "dispatched", errorType: null);
                NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
                NimBusMeters.OutboxDispatched.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogDebug(
                    "Outbox dispatched message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId})",
                    outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                return true;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var errorType = ex.GetType().FullName;
                var tags = BuildDispatchTags(endpoint, "failed", errorType);
                NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
                NimBusMeters.OutboxDispatched.Add(1, tags);
                if (activity is not null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.SetTag(MessagingAttributes.ErrorType, errorType);
                    activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                    {
                        { "exception.type", errorType },
                        { "exception.message", ex.Message },
                        { "exception.stacktrace", ex.ToString() },
                    }));
                }

                _logger.LogError(
                    ex,
                    "Outbox dispatch failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}). Halting this poll; will retry next interval.",
                    outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                return false;
            }
            finally
            {
                activity?.Dispose();
                Activity.Current = savedCurrent;
            }
        }

        private static ActivityLink? TryBuildLink(string traceParent, string traceState)
        {
            var context = W3CMessagePropagator.TryParse(traceParent, traceState);
            return context == default ? (ActivityLink?)null : new ActivityLink(context);
        }

        private static KeyValuePair<string, object?>[] BuildDispatchTags(string? endpoint, string outcome, string? errorType)
        {
            var tags = new List<KeyValuePair<string, object?>>(3)
            {
                new(MessagingAttributes.NimBusOutcome, outcome),
            };
            if (!string.IsNullOrEmpty(endpoint))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
            if (!string.IsNullOrEmpty(errorType))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
            return tags.ToArray();
        }
    }
}
