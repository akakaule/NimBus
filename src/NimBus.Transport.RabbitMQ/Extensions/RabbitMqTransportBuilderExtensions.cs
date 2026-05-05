using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ.Connection;
using NimBus.Transport.RabbitMQ.Topology;

namespace NimBus.Transport.RabbitMQ.Extensions;

/// <summary>
/// Provider registration entry point for the RabbitMQ transport. Consumers call
/// <see cref="AddRabbitMqTransport"/> from inside their <c>AddNimBus</c>
/// configuration callback to satisfy the transport-provider validation gate
/// and wire the broker-side surface into DI.
/// </summary>
public static class RabbitMqTransportBuilderExtensions
{
    /// <summary>
    /// Registers RabbitMQ as the active NimBus transport. Adds the marker
    /// <see cref="ITransportProviderRegistration"/>, the
    /// <see cref="ITransportCapabilities"/> descriptor, the
    /// <see cref="ITransportManagement"/> adapter, the configuration shape, the
    /// shared <see cref="RabbitMqConnectionFactory"/>, and the per-endpoint
    /// <see cref="ISender"/> factory consumers (publisher / subscriber
    /// registrations) resolve via DI.
    /// </summary>
    /// <param name="builder">The NimBus builder.</param>
    /// <param name="configure">Optional <see cref="RabbitMqTransportOptions"/> configurator.</param>
    public static INimBusBuilder AddRabbitMqTransport(
        this INimBusBuilder builder,
        Action<RabbitMqTransportOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var services = builder.Services;

        var optionsBuilder = services.AddOptions<RabbitMqTransportOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.Validate(
            o => o.PartitionsPerEndpoint > 0,
            "RabbitMqTransportOptions.PartitionsPerEndpoint must be greater than zero.");
        optionsBuilder.Validate(
            o => o.MaxDeliveryCount > 0,
            "RabbitMqTransportOptions.MaxDeliveryCount must be greater than zero.");
        optionsBuilder.Validate(
            o => !string.IsNullOrWhiteSpace(o.Uri) || !string.IsNullOrWhiteSpace(o.HostName),
            "RabbitMqTransportOptions requires either Uri or HostName.");

        services.AddSingleton<ITransportProviderRegistration>(_ => new RabbitMqTransportProviderRegistration());
        services.AddSingleton<ITransportCapabilities, RabbitMqTransportCapabilities>();

        services.TryAddSingleton<RabbitMqConnectionFactory>();

        services.TryAddSingleton<Func<string, ISender>>(sp =>
        {
            var connectionFactory = sp.GetRequiredService<RabbitMqConnectionFactory>();
            return endpoint => new RabbitMqSender(connectionFactory, endpoint);
        });

        services.AddSingleton<ITransportManagement, RabbitMqTransportManagement>();
        services.AddSingleton<ITransportSessionOps, RabbitMqSessionOps>();

        return builder;
    }
}
