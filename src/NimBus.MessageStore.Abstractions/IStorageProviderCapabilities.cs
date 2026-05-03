namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Optional provider-specific capabilities consumers can branch on. Lets operator
/// tooling (cross-account copy, container management) light up only when the active
/// provider supports it, and surface a clear error otherwise.
/// </summary>
public interface IStorageProviderCapabilities
{
    /// <summary>
    /// True when the provider supports the cross-account / cross-database copy
    /// operations exposed by AdminService and the CLI. Cosmos returns true; SQL
    /// Server returns false (operators use SQL-native backup/restore tooling instead).
    /// </summary>
    bool SupportsCrossAccountCopy { get; }
}
