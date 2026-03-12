using Microsoft.Extensions.DependencyInjection;
using System;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Extension methods for registering NimBus services.
    /// </summary>
    public static class NimBusServiceCollectionExtensions
    {
        /// <summary>
        /// Adds NimBus core services and configures extensions via the builder.
        /// </summary>
        /// <example>
        /// <code>
        /// services.AddNimBus(builder =>
        /// {
        ///     builder.AddPipelineBehavior&lt;LoggingBehavior&gt;();
        ///     builder.AddLifecycleObserver&lt;MetricsObserver&gt;();
        ///     builder.AddExtension&lt;NotificationsExtension&gt;();
        /// });
        /// </code>
        /// </example>
        public static IServiceCollection AddNimBus(this IServiceCollection services, Action<INimBusBuilder> configure = null)
        {
            var builder = new NimBusBuilder(services);
            configure?.Invoke(builder);
            builder.Build();
            return services;
        }
    }
}
