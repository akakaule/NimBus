namespace NimBus.Transport.Abstractions;

/// <summary>
/// Marker interface registered exactly once per active transport provider. The NimBus
/// builder enumerates all <see cref="ITransportProviderRegistration"/> registrations at
/// <c>Build()</c> time and fails fast when zero or more than one provider is present.
/// Provider packages register a singleton implementation as part of their
/// <c>Add{Provider}Transport()</c> extension method (for example
/// <c>AddServiceBusTransport()</c>, <c>AddRabbitMqTransport()</c>, or
/// <c>AddInMemoryTransport()</c>).
/// </summary>
public interface ITransportProviderRegistration
{
    /// <summary>
    /// Human-readable provider name, used in error messages and diagnostics.
    /// Examples: "Azure Service Bus", "RabbitMQ", "InMemory".
    /// </summary>
    string ProviderName { get; }
}
