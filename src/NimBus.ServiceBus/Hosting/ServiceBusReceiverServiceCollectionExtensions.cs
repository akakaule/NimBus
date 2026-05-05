using System;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NimBus.ServiceBus.Hosting;

/// <summary>
/// DI extensions for the Service Bus receiver hosted service. The receiver runs
/// a <see cref="ServiceBusSessionProcessor"/> against a topic/subscription and
/// delegates each message to the registered <see cref="IServiceBusAdapter"/>
/// (typically the SDK's <c>SubscriberClient</c>).
/// </summary>
public static class ServiceBusReceiverServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Service Bus receiver as a hosted service that listens to the
    /// given topic/subscription. Requires <see cref="ServiceBusClient"/> and an
    /// <see cref="IServiceBusAdapter"/> to already be registered (the latter is
    /// satisfied by <c>AddNimBusSubscriber</c> in the SDK).
    /// </summary>
    public static IServiceCollection AddServiceBusReceiver(
        this IServiceCollection services,
        Action<ServiceBusReceiverOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var options = new ServiceBusReceiverOptions();
        configure(options);

        if (string.IsNullOrEmpty(options.TopicName))
            throw new ArgumentException("TopicName must be specified.", nameof(configure));
        if (string.IsNullOrEmpty(options.SubscriptionName))
            throw new ArgumentException("SubscriptionName must be specified.", nameof(configure));

        services.AddHostedService(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            var adapter = sp.GetRequiredService<IServiceBusAdapter>();
            var logger = sp.GetRequiredService<ILogger<ServiceBusReceiverHostedService>>();
            return new ServiceBusReceiverHostedService(client, adapter, options, logger);
        });

        return services;
    }
}
