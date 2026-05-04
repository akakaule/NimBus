using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing;

/// <summary>
/// Test-only registration that satisfies <see cref="IStorageProviderRegistration"/>
/// for the NimBus builder validation. Tests that compose just the pipeline and
/// lifecycle infrastructure (and don't actually persist messages) can call
/// <see cref="AddInMemoryMessageStore(INimBusBuilder)"/> to bypass storage setup.
/// The conformance suite (NimBus.Testing.Conformance) provides full in-memory
/// implementations of the four storage contracts for tests that need them.
/// </summary>
public static class InMemoryMessageStoreServiceCollectionExtensions
{
    public static INimBusBuilder AddInMemoryMessageStore(this INimBusBuilder builder)
    {
        builder.Services.AddSingleton<IStorageProviderRegistration>(_ => new InMemoryStorageProviderRegistration());
        builder.Services.AddSingleton<IStorageProviderCapabilities>(_ => new InMemoryStorageProviderCapabilities());
        builder.Services.AddSingleton<ISessionStateStore, InMemorySessionStateStore>();
        return builder;
    }

    private sealed class InMemoryStorageProviderRegistration : IStorageProviderRegistration
    {
        public string ProviderName => "InMemory";
    }

    private sealed class InMemoryStorageProviderCapabilities : IStorageProviderCapabilities
    {
        public bool SupportsCrossAccountCopy => false;
    }
}
