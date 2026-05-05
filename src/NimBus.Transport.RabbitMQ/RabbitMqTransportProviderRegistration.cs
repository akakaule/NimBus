using NimBus.Transport.Abstractions;

namespace NimBus.Transport.RabbitMQ;

/// <summary>
/// Marker registration for the RabbitMQ transport. Picked up by
/// <c>NimBusBuilder.ValidateTransportProvider</c> to confirm exactly one transport
/// provider is wired per running application instance.
/// </summary>
internal sealed class RabbitMqTransportProviderRegistration : ITransportProviderRegistration
{
    public string ProviderName => "RabbitMQ";
}
