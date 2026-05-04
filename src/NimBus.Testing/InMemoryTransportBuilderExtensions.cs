using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Transport.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// Provider-aware registration for the in-memory test transport. Single entry point
/// consumers should call when wiring NimBus against the <see cref="InMemoryMessageBus"/>
/// in unit / integration tests. Registers the transport-provider marker (consumed by
/// builder validation), capabilities, and the in-memory bus as <see cref="ISender"/>.
/// </summary>
public static class InMemoryTransportBuilderExtensions
{
    /// <summary>
    /// Registers the in-memory transport. Idempotent on the bus singleton — repeated
    /// calls reuse the existing <see cref="InMemoryMessageBus"/> registration so a test
    /// fixture can resolve the same instance regardless of registration order.
    /// </summary>
    public static INimBusBuilder AddInMemoryTransport(this INimBusBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton<InMemoryMessageBus>();
        services.AddSingleton<ISender>(sp => sp.GetRequiredService<InMemoryMessageBus>());

        services.AddSingleton<ITransportProviderRegistration>(_ => new InMemoryTransportProviderRegistration());
        services.AddSingleton<ITransportCapabilities>(_ => new InMemoryTransportCapabilities());

        // TODO(#14 follow-up): wire InMemoryMessageContext factory here once the
        // disentangler's constructor changes (task #14 / #16 follow-up A) have landed.
        // Until then the bus instantiates contexts directly inside its DeliverAll loop;
        // moving that to a DI-resolved factory belongs to the same pass that wires
        // ISessionStateStore through the bridges.

        return builder;
    }
}
