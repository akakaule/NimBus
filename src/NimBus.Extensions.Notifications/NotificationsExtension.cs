using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;
using System;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// NimBus extension that adds notification support on message lifecycle events.
    /// </summary>
    public class NotificationsExtension : INimBusExtension
    {
        private readonly Action<NotificationOptions> _configureOptions;
        private readonly Action<IServiceCollection> _configureChannels;

        public NotificationsExtension()
            : this(null, null)
        {
        }

        public NotificationsExtension(Action<NotificationOptions> configureOptions, Action<IServiceCollection> configureChannels)
        {
            _configureOptions = configureOptions;
            _configureChannels = configureChannels;
        }

        public void Configure(INimBusBuilder builder)
        {
            var options = new NotificationOptions();
            _configureOptions?.Invoke(options);
            builder.Services.AddSingleton(options);

            // Register channels - use console channel as default if none configured
            if (_configureChannels != null)
            {
                _configureChannels(builder.Services);
            }
            else
            {
                builder.Services.AddSingleton<INotificationChannel, ConsoleNotificationChannel>();
            }

            // Register the lifecycle observer
            builder.AddLifecycleObserver<NotificationLifecycleObserver>();
        }
    }

    /// <summary>
    /// Extension methods for adding Notifications to the NimBus builder.
    /// </summary>
    public static class NotificationsBuilderExtensions
    {
        /// <summary>
        /// Adds notification support with default options and console output.
        /// </summary>
        public static INimBusBuilder AddNotifications(this INimBusBuilder builder)
        {
            return builder.AddExtension(new NotificationsExtension());
        }

        /// <summary>
        /// Adds notification support with custom configuration.
        /// </summary>
        /// <param name="builder">The NimBus builder.</param>
        /// <param name="configureOptions">Configure notification options (which events trigger notifications).</param>
        /// <param name="configureChannels">Register custom notification channels (email, Teams, Slack, etc.).</param>
        public static INimBusBuilder AddNotifications(
            this INimBusBuilder builder,
            Action<NotificationOptions> configureOptions = null,
            Action<IServiceCollection> configureChannels = null)
        {
            return builder.AddExtension(new NotificationsExtension(configureOptions, configureChannels));
        }
    }
}
