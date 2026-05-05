using System;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Management.ServiceBus;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Provider registration entry point for the Azure Service Bus transport. Consumers
/// call <see cref="AddServiceBusTransport"/> from inside their <c>AddNimBus</c>
/// configuration callback to satisfy the transport-provider validation gate and
/// wire the Service Bus client surface into DI.
/// </summary>
public static class ServiceBusTransportBuilderExtensions
{
    /// <summary>
    /// Registers Azure Service Bus as the active NimBus transport. Adds the marker
    /// <see cref="ITransportProviderRegistration"/>, the
    /// <see cref="ITransportCapabilities"/> descriptor, the
    /// <see cref="ITransportManagement"/> adapter, the
    /// <see cref="ServiceBusTransportOptions"/> configuration shape, and the
    /// underlying <see cref="ServiceBusClient"/> /
    /// <see cref="ServiceBusAdministrationClient"/> /
    /// <see cref="IServiceBusManagement"/> services that consumers (Resolver,
    /// WebApp, CLI) resolve via DI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connection material is read from <see cref="ServiceBusTransportOptions"/>
    /// (set via <paramref name="configure"/>) or, when those are blank, from the
    /// host's <see cref="IConfiguration"/> using the same probe order the WebApp
    /// and Resolver have used historically:
    /// <list type="number">
    /// <item><description><c>AzureWebJobsServiceBus__fullyQualifiedNamespace</c> — fully-qualified namespace; pairs with <c>DefaultAzureCredential</c> when it does not contain <c>SharedAccessKey=</c>.</description></item>
    /// <item><description><c>ConnectionStrings:servicebus</c></description></item>
    /// <item><description><c>AzureWebJobsServiceBus</c></description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Client registrations use <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type, Type)"/>
    /// so they cooperate with hosts (e.g. .NET Aspire) that register
    /// <c>ServiceBusClient</c> upstream. The transport-provider marker and
    /// capabilities are registered unconditionally — the builder validates that
    /// exactly one provider is active.
    /// </para>
    /// </remarks>
    /// <param name="builder">The NimBus builder.</param>
    /// <param name="configure">Optional <see cref="ServiceBusTransportOptions"/> configurator.</param>
    public static INimBusBuilder AddServiceBusTransport(
        this INimBusBuilder builder,
        Action<ServiceBusTransportOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var services = builder.Services;

        var optionsBuilder = services.AddOptions<ServiceBusTransportOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                if (string.IsNullOrEmpty(options.FullyQualifiedNamespace) && string.IsNullOrEmpty(options.ConnectionString))
                {
                    var fqns = configuration.GetValue<string>("AzureWebJobsServiceBus__fullyQualifiedNamespace");
                    if (!string.IsNullOrEmpty(fqns) && !fqns.Contains("SharedAccessKey="))
                    {
                        options.FullyQualifiedNamespace = fqns;
                    }
                    else
                    {
                        options.ConnectionString =
                            fqns
                            ?? configuration.GetConnectionString("servicebus")
                            ?? configuration.GetValue<string>("AzureWebJobsServiceBus");
                    }
                }
            });

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.Validate(
            o => !string.IsNullOrWhiteSpace(o.ConnectionString) || !string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace),
            "ServiceBusTransportOptions requires either ConnectionString or FullyQualifiedNamespace " +
            "(set 'AzureWebJobsServiceBus__fullyQualifiedNamespace', the 'servicebus' connection string, " +
            "or 'AzureWebJobsServiceBus').");

        services.AddSingleton<ITransportProviderRegistration>(_ => new ServiceBusTransportProviderRegistration());
        services.AddSingleton<ITransportCapabilities>(_ => new ServiceBusTransportCapabilities());

        services.TryAddSingleton<ServiceBusClient>(sp => CreateServiceBusClient(sp));
        services.TryAddSingleton<ServiceBusAdministrationClient>(sp => CreateServiceBusAdministrationClient(sp));
        services.TryAddSingleton<IServiceBusManagement>(sp =>
            new ServiceBusManagement(sp.GetRequiredService<ServiceBusAdministrationClient>()));

        // Per-endpoint ISender factory — the transport-neutral surface SDK
        // publisher/subscriber registrations consume to send to a topic by name.
        // RabbitMQ provider will register the same Func<string, ISender>
        // signature with its own queue-bound implementation.
        services.TryAddSingleton<Func<string, ISender>>(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            return endpoint => new Sender(client.CreateSender(endpoint));
        });

        services.AddSingleton<ITransportManagement, ServiceBusTransportManagement>();

        return builder;
    }

    private static ServiceBusClient CreateServiceBusClient(IServiceProvider sp)
    {
        var options = ResolveOptions(sp);
        return !string.IsNullOrEmpty(options.FullyQualifiedNamespace)
            ? new ServiceBusClient(options.FullyQualifiedNamespace, options.Credential ?? new DefaultAzureCredential())
            : new ServiceBusClient(options.ConnectionString);
    }

    private static ServiceBusAdministrationClient CreateServiceBusAdministrationClient(IServiceProvider sp)
    {
        var options = ResolveOptions(sp);
        return !string.IsNullOrEmpty(options.FullyQualifiedNamespace)
            ? new ServiceBusAdministrationClient(options.FullyQualifiedNamespace, options.Credential ?? new DefaultAzureCredential())
            : new ServiceBusAdministrationClient(options.ConnectionString);
    }

    private static ServiceBusTransportOptions ResolveOptions(IServiceProvider sp)
        => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceBusTransportOptions>>().Value;
}
