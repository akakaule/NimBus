using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.SDK.EventHandlers
{
    /// <summary>
    /// <see cref="IEventContextHandler"/> decorator active only when a subscriber has
    /// enabled CloudEvents. For an inbound CloudEvent it validates the required
    /// CloudEvents 1.0 attributes (<c>id</c>, <c>source</c>, <c>type</c>,
    /// <c>specversion</c> == "1.0") and that the resolved <c>type</c> maps to a
    /// registered event contract. Any violation is raised as a
    /// <see cref="PermanentFailureException"/> so the message dead-letters with a
    /// clear, inspectable reason (never silently dropped, never crashing the
    /// processor). Native (non-CloudEvents) messages are delegated to the inner
    /// handler unchanged.
    /// </summary>
    public sealed class CloudEventValidatingContextHandler : IEventContextHandler
    {
        private readonly IEventContextHandler _inner;

        /// <summary>Creates the validating decorator around <paramref name="inner"/>.</summary>
        public CloudEventValidatingContextHandler(IEventContextHandler inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc/>
        public async Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            var cloudEvent = context.GetCloudEvent();
            if (cloudEvent is null)
            {
                // Native NimBus message — no CloudEvents validation, unchanged behavior.
                await _inner.Handle(context, cancellationToken);
                return;
            }

            ValidateRequiredAttributes(cloudEvent);

            try
            {
                await _inner.Handle(context, cancellationToken);
            }
            catch (EventHandlerNotFoundException notFound)
            {
                // An unknown CloudEvents type must dead-letter (AC13), not send an
                // UnsupportedResponse the way a native unknown type does.
                throw new PermanentFailureException(
                    new InvalidCloudEventException(
                        $"CloudEvents type '{cloudEvent.Type}' maps to no registered event contract.", notFound));
            }
        }

        private static void ValidateRequiredAttributes(CloudEvent cloudEvent)
        {
            if (string.IsNullOrEmpty(cloudEvent.Id))
                throw Reject("id");
            if (string.IsNullOrEmpty(cloudEvent.Source))
                throw Reject("source");
            if (string.IsNullOrEmpty(cloudEvent.Type))
                throw Reject("type");
            if (!string.Equals(cloudEvent.SpecVersion, CloudEvent.CloudEventsSpecVersion, StringComparison.Ordinal))
                throw new PermanentFailureException(
                    new InvalidCloudEventException(
                        $"CloudEvents message has unsupported specversion '{cloudEvent.SpecVersion ?? "<null>"}' (expected '{CloudEvent.CloudEventsSpecVersion}')."));
        }

        private static PermanentFailureException Reject(string attribute) =>
            new(new InvalidCloudEventException($"CloudEvents message missing required attribute '{attribute}'."));
    }
}
