using System;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Pairs a registered <see cref="INotificationChannel"/> with its options so the
    /// <see cref="NotificationRouter"/> can read each channel's <see cref="NotificationChannelOptions.MinSeverity"/>.
    /// </summary>
    public sealed class ChannelRegistration
    {
        public ChannelRegistration(INotificationChannel channel, NotificationChannelOptions options)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The channel that delivers the notification.</summary>
        public INotificationChannel Channel { get; }

        /// <summary>The channel's options, carrying its <see cref="NotificationChannelOptions.MinSeverity"/>.</summary>
        public NotificationChannelOptions Options { get; }
    }
}
