using System;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Provider registration entry point for the Azure Service Bus transport. Consumers
/// call <see cref="AddServiceBusTransport"/> from inside their <c>AddNimBus</c>
/// configuration callback to satisfy the transport-provider validation gate.
/// </summary>
public static class ServiceBusTransportBuilderExtensions
{
    /// <summary>
    /// Registers Azure Service Bus as the active NimBus transport. Adds the marker
    /// <see cref="ITransportProviderRegistration"/>, the
    /// <see cref="ITransportCapabilities"/> descriptor, and the
    /// <see cref="ServiceBusTransportOptions"/> configuration shape. The actual
    /// <c>ServiceBusClient</c> + <c>ServiceBusAdapter</c> wiring is added in a
    /// follow-up commit (see TODO below) — this method intentionally only scaffolds
    /// the provider-selection surface so it can land in parallel with the
    /// disentanglement constructor cascade.
    /// </summary>
    /// <param name="builder">The NimBus builder.</param>
    /// <param name="configure">Optional <see cref="ServiceBusTransportOptions"/> configurator.</param>
    public static INimBusBuilder AddServiceBusTransport(
        this INimBusBuilder builder,
        Action<ServiceBusTransportOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var services = builder.Services;

        var optionsBuilder = services.AddOptions<ServiceBusTransportOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<ITransportProviderRegistration>(_ => new ServiceBusTransportProviderRegistration());
        services.AddSingleton<ITransportCapabilities>(_ => new ServiceBusTransportCapabilities());
        services.AddSingleton<ITransportManagement, ServiceBusTransportManagement>();

        // TODO(#3 / #14): wire ServiceBusClient + ServiceBusAdapter here once the
        // constructor cascade settles. This scaffold only registers the provider
        // marker, capabilities, options, and the management adapter so existing
        // hosts can drop their WithoutTransport() opt-outs.

        return builder;
    }
}
