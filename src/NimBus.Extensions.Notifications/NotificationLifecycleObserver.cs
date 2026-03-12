using NimBus.Core.Extensions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Lifecycle observer that sends notifications to registered channels
    /// when messages fail or are dead-lettered.
    /// </summary>
    public class NotificationLifecycleObserver : IMessageLifecycleObserver
    {
        private readonly IEnumerable<INotificationChannel> _channels;
        private readonly NotificationOptions _options;

        public NotificationLifecycleObserver(IEnumerable<INotificationChannel> channels, NotificationOptions options)
        {
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            if (!_options.NotifyOnReceived) return;

            var notification = new Notification
            {
                Severity = NotificationSeverity.Information,
                Title = $"Message received: {context.EventTypeId}",
                Message = $"Message {context.MessageId} received for event {context.EventId}.",
                EventId = context.EventId,
                EventTypeId = context.EventTypeId,
                MessageId = context.MessageId,
                CorrelationId = context.CorrelationId,
            };

            await SendToAllChannels(notification, cancellationToken);
        }

        public async Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            if (!_options.NotifyOnCompleted) return;

            var notification = new Notification
            {
                Severity = NotificationSeverity.Information,
                Title = $"Message completed: {context.EventTypeId}",
                Message = $"Message {context.MessageId} for event {context.EventId} completed successfully.",
                EventId = context.EventId,
                EventTypeId = context.EventTypeId,
                MessageId = context.MessageId,
                CorrelationId = context.CorrelationId,
            };

            await SendToAllChannels(notification, cancellationToken);
        }

        public async Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default)
        {
            if (!_options.NotifyOnFailure) return;

            var notification = new Notification
            {
                Severity = NotificationSeverity.Error,
                Title = $"Message failed: {context.EventTypeId}",
                Message = $"Message {context.MessageId} for event {context.EventId} failed with error: {exception?.Message}",
                EventId = context.EventId,
                EventTypeId = context.EventTypeId,
                MessageId = context.MessageId,
                CorrelationId = context.CorrelationId,
                ErrorDetails = exception?.ToString(),
            };

            await SendToAllChannels(notification, cancellationToken);
        }

        public async Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception exception = null, CancellationToken cancellationToken = default)
        {
            if (!_options.NotifyOnDeadLetter) return;

            var notification = new Notification
            {
                Severity = NotificationSeverity.Critical,
                Title = $"Message dead-lettered: {context.EventTypeId}",
                Message = $"Message {context.MessageId} for event {context.EventId} was dead-lettered. Reason: {reason}",
                EventId = context.EventId,
                EventTypeId = context.EventTypeId,
                MessageId = context.MessageId,
                CorrelationId = context.CorrelationId,
                ErrorDetails = exception?.ToString(),
            };

            await SendToAllChannels(notification, cancellationToken);
        }

        private async Task SendToAllChannels(Notification notification, CancellationToken cancellationToken)
        {
            foreach (var channel in _channels)
            {
                try
                {
                    await channel.SendAsync(notification, cancellationToken);
                }
                catch
                {
                    // Don't let notification failures affect message processing
                }
            }
        }
    }
}
