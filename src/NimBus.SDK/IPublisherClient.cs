using System;
using System.Collections.Generic;
using System.Threading;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System.Threading.Tasks;

namespace NimBus.SDK
{
    public interface IPublisherClient
    {
        Task Publish(IEvent @event);

        /// <summary>
        /// Publishes a pre-built, dynamically-typed message (no compiled IEvent). The caller sets
        /// EventTypeId + MessageContent.EventContent. Used by the agent REST API (spec 022).
        /// </summary>
        Task Publish(IMessage message, CancellationToken cancellationToken = default);

        Task Publish(IEvent @event, string sessionId, string correlationId);

        Task Publish(IEvent @event, string sessionId, string correlationId, string messageId);

        /// <summary>
        /// Publishes a workflow follow-up using the inbound handler context's session,
        /// correlation, and lineage metadata. The outgoing parent is the inbound
        /// <see cref="IEventHandlerContext.MessageId"/>. The originating message is
        /// preserved across later hops and falls back to the inbound message for a
        /// first hop whose legacy lineage is absent.
        /// </summary>
        /// <param name="event">The command or event to publish.</param>
        /// <param name="context">The context of the inbound message causing the follow-up.</param>
        /// <param name="messageId">
        /// An explicit deterministic identifier for the logical transition. Derive it
        /// from durable workflow identity, transition name, and version or attempt, and
        /// reproduce the same value when retrying that transition.
        /// </param>
        /// <param name="cancellationToken">Cancellation token propagated to the configured sender.</param>
        /// <returns>A task that completes when the message has been sent or stored in the outbox.</returns>
        /// <exception cref="NotSupportedException">
        /// The publisher implementation does not support context-aware publishing.
        /// </exception>
        Task PublishFromContext(
            IEvent @event,
            IEventHandlerContext context,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Context-aware workflow publishing requires a compatible publisher implementation. Use PublisherClient or update the custom IPublisherClient implementation.");
        }

        /// <summary>
        /// Schedules a workflow timeout event for delivery at
        /// <paramref name="scheduledEnqueueTime"/>, carrying the inbound context's
        /// session, correlation, and lineage metadata like
        /// <see cref="PublishFromContext"/>. <paramref name="timeoutId"/> is the
        /// logical timeout identity: it is stamped as both the first delivery's
        /// MessageId and the <c>ScheduledMessageId</c> marker on every delivery, so
        /// it must be deterministic (derive it from durable workflow identity,
        /// transition name, and generation) and at most 128 characters. A past due
        /// time is allowed and means immediately eligible.
        /// Persist the returned handle: it is required to cancel the schedule and
        /// is only valid with the same endpoint-bound publisher configuration that
        /// created it. The default implementation throws
        /// <see cref="NotSupportedException"/> so existing custom implementations
        /// stay source- and binary-compatible.
        /// </summary>
        /// <param name="event">The timeout event to schedule.</param>
        /// <param name="scheduledEnqueueTime">The due time; normalized to UTC.</param>
        /// <param name="context">The inbound handler context supplying workflow identity and lineage.</param>
        /// <param name="timeoutId">The deterministic logical timeout identity.</param>
        /// <param name="cancellationToken">Cancellation token propagated to the configured sender.</param>
        /// <returns>The handle identifying the schedule for later cancellation.</returns>
        Task<ScheduledMessageHandle> Schedule(
            IEvent @event,
            DateTimeOffset scheduledEnqueueTime,
            IEventHandlerContext context,
            string timeoutId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Scheduled workflow timeouts require a compatible publisher implementation. Use PublisherClient or update the custom IPublisherClient implementation.");
        }

        /// <summary>
        /// Cancels a scheduled timeout by handle. Cancellation is an optimization,
        /// not the correctness boundary: a timeout may still be delivered after a
        /// successful cancellation request (direct mode) or when dispatch already
        /// started (outbox mode) — the handler's durable workflow-state guard
        /// remains the final authority. The default implementation throws
        /// <see cref="NotSupportedException"/> so existing custom implementations
        /// stay source- and binary-compatible.
        /// </summary>
        /// <param name="handle">The handle returned by <see cref="Schedule(IEvent, DateTimeOffset, IEventHandlerContext, string, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancellation token propagated to the configured sender.</param>
        /// <returns>The transport-specific cancellation outcome.</returns>
        Task<ScheduledMessageCancellationOutcome> CancelScheduled(
            ScheduledMessageHandle handle,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Scheduled workflow timeouts require a compatible publisher implementation. Use PublisherClient or update the custom IPublisherClient implementation.");
        }

        /// <summary>
        /// Pre-release - use with care!
        /// Publish multiple messages at once. Make sure batch size enforced by Azure Service Bus is taken into account.
        /// </summary>
        /// <param name="events">List of events you want to publish. Make sure to make them before publishing</param>
        /// <param name="correlationId"></param>
        /// <returns></returns>
        Task PublishBatch(IEnumerable<IEvent> events, string correlationId = null);
        /// <summary>
        /// Use to get batch of maximum possible size supported by Azure Service Bus
        /// </summary>
        /// <param name="events">Events you want to split into multiple batches</param>
        /// <returns>Batches of events</returns>
        IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events);

        /// <summary>
        /// Publishes any number of events, automatically split into pages that
        /// fit the Azure Service Bus batch size. Preferred over
        /// <see cref="GetBatches"/> + <see cref="PublishBatch"/> — the
        /// <see cref="PublisherClient"/> implementation builds and serializes
        /// each event exactly once. This default implementation delegates to
        /// GetBatches + PublishBatch so existing test doubles keep working.
        /// </summary>
        /// <param name="events">Events to publish, in order.</param>
        /// <param name="correlationId">Correlation id applied to every message; a new GUID when null.</param>
        async Task PublishBatches(IEnumerable<IEvent> events, string correlationId = null)
        {
            foreach (var batch in GetBatches(new List<IEvent>(events)))
            {
                await PublishBatch(batch, correlationId);
            }
        }

        /// <summary>
        /// Sends a request and awaits a typed response with timeout.
        /// Uses Azure Service Bus sessions for reply correlation.
        /// </summary>
        Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
            where TRequest : IEvent
            where TResponse : class
        {
            throw new NotSupportedException("Request/response requires a ServiceBusClient. Use PublisherClient with a ServiceBusClient constructor.");
        }
    }
}
