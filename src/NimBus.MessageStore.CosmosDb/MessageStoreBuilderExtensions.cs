using System;
using NimBus.Core.Extensions;

namespace NimBus.MessageStore;

/// <summary>
/// Legacy registration entry point. Preserved for source-compatibility with deployments
/// that still call <c>AddMessageStore()</c>. New code should call
/// <see cref="CosmosDbMessageStoreBuilderExtensions.AddCosmosDbMessageStore(INimBusBuilder)"/> directly.
/// </summary>
public static class MessageStoreBuilderExtensions
{
    /// <summary>
    /// Adds Cosmos DB-backed message store services to the NimBus builder. Equivalent
    /// to <c>AddCosmosDbMessageStore()</c>. Marked obsolete because the implicit
    /// "MessageStore == Cosmos" coupling no longer holds: NimBus now supports
    /// pluggable storage providers and consumers must opt in to a specific provider.
    /// </summary>
    [Obsolete("Use AddCosmosDbMessageStore() instead. AddMessageStore() will be removed in a future major version.")]
    public static INimBusBuilder AddMessageStore(this INimBusBuilder builder)
        => builder.AddCosmosDbMessageStore();
}
