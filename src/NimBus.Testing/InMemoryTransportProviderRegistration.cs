using NimBus.Transport.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// Marker registration for the in-memory transport. Registered as a singleton by
/// <see cref="InMemoryTransportBuilderExtensions.AddInMemoryTransport"/> and discovered by the
/// NimBus builder's <c>ValidateTransportProvider()</c> check, which requires exactly one
/// <see cref="ITransportProviderRegistration"/> per running application.
/// </summary>
public sealed class InMemoryTransportProviderRegistration : ITransportProviderRegistration
{
    /// <inheritdoc />
    public string ProviderName => "InMemory";
}
