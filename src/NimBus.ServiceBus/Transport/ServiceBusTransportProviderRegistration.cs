using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Marker registration for the Azure Service Bus transport. Picked up by
/// <c>NimBusBuilder.ValidateTransportProvider</c> to confirm exactly one transport
/// provider is wired per running application instance.
/// </summary>
internal sealed class ServiceBusTransportProviderRegistration : ITransportProviderRegistration
{
    public string ProviderName => "Azure Service Bus";
}
