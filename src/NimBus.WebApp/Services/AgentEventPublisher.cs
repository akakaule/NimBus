using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using NimBus.Core.Messages;
using NimBus.SDK;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services
{
    /// <summary>
    /// Production <see cref="IAgentEventPublisher"/>: publishes a classless agent event
    /// onto the Agent Zone topic via the instrumented SDK <see cref="PublisherClient"/>.
    /// The zone endpoint id is read from config key <c>Agent:ZoneEndpointId</c>
    /// (default <c>"AgentZoneEndpoint"</c>).
    /// </summary>
    public sealed class AgentEventPublisher : IAgentEventPublisher
    {
        private readonly ServiceBusClient _serviceBusClient;
        private readonly string _zoneEndpointId;

        public AgentEventPublisher(ServiceBusClient serviceBusClient, IConfiguration config)
        {
            _serviceBusClient = serviceBusClient;
            _zoneEndpointId = config["Agent:ZoneEndpointId"] ?? "AgentZoneEndpoint";
        }

        public async Task PublishAsync(IMessage message, CancellationToken cancellationToken = default)
        {
            var publisher = await PublisherClient.CreateAsync(_serviceBusClient, _zoneEndpointId, cancellationToken);
            await publisher.Publish(message, cancellationToken);
        }
    }
}
