namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Marker interface registered exactly once per active storage provider. The NimBus
/// builder enumerates all <see cref="IStorageProviderRegistration"/> registrations at
/// <c>Build()</c> time and fails fast when zero or more than one provider is present.
/// Provider packages register a singleton implementation as part of their
/// <c>Add{Provider}MessageStore()</c> extension method.
/// </summary>
public interface IStorageProviderRegistration
{
    /// <summary>
    /// Human-readable provider name, used in error messages and diagnostics.
    /// Examples: "Cosmos DB", "SQL Server", "InMemory".
    /// </summary>
    string ProviderName { get; }
}
