using Microsoft.Extensions.Options;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ;
using NimBus.Transport.RabbitMQ.Connection;
using NimBus.Transport.RabbitMQ.Topology;

namespace NimBus.CommandLine;

/// <summary>
/// Drives <see cref="RabbitMqTransportManagement"/> from a
/// <see cref="PlatformConfiguration"/> so <c>nb topology apply --transport rabbitmq</c>
/// declares the same logical endpoints the Service Bus path declares, just on
/// a different broker.
/// </summary>
internal sealed class RabbitMqTopologyProvisioner
{
    private readonly RabbitMqTransportOptions _options;
    private readonly Func<IPlatform> _platformFactory;
    private readonly Func<RabbitMqTransportOptions, ITransportManagement> _managementFactory;

    public RabbitMqTopologyProvisioner(RabbitMqTransportOptions options)
        : this(options, static () => new PlatformConfiguration(), CreateDefaultManagement)
    {
    }

    internal RabbitMqTopologyProvisioner(
        RabbitMqTransportOptions options,
        Func<IPlatform> platformFactory,
        Func<RabbitMqTransportOptions, ITransportManagement> managementFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        _managementFactory = managementFactory ?? throw new ArgumentNullException(nameof(managementFactory));
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var platform = _platformFactory();
        var management = _managementFactory(_options);

        // The Resolver endpoint is implicit on Service Bus (a topic); on RabbitMQ
        // it is explicit because exchanges + DLX + delayed exchange + N partition
        // queues all need to be declared up-front. Keep the same naming so
        // operators see the same "ResolverId" entity in both transports.
        await management.DeclareEndpointAsync(
            new EndpointConfig(Constants.ResolverId, RequiresOrderedDelivery: true, MaxConcurrency: null),
            cancellationToken).ConfigureAwait(false);
        CliOutput.WriteLine($"Declared RabbitMQ topology for endpoint '{Constants.ResolverId}'.");

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await management.DeclareEndpointAsync(
                new EndpointConfig(endpoint.Id, RequiresOrderedDelivery: true, MaxConcurrency: null),
                cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine($"Declared RabbitMQ topology for endpoint '{endpoint.Id}'.");
        }
    }

    private static ITransportManagement CreateDefaultManagement(RabbitMqTransportOptions options)
    {
        var connectionFactory = new RabbitMqConnectionFactory(Options.Create(options));
        return new RabbitMqTransportManagement(connectionFactory, Options.Create(options));
    }
}
