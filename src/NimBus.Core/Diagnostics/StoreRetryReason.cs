namespace NimBus.Core.Diagnostics;

/// <summary>
/// Values for the <see cref="MessagingAttributes.NimBusStoreReason"/> attribute on
/// <see cref="NimBusMeters.ResolverStoreRetry"/>: why the Resolver could not persist a message
/// on this attempt.
/// </summary>
public static class StoreRetryReason
{
    /// <summary>The store rate-limited the write (Cosmos DB 429, <c>RequestLimitException</c>).</summary>
    public const string Throttled = "throttled";

    /// <summary>Any other transient store failure (<c>StorageProviderTransientException</c>: connectivity, 5xx, SQL transient errors).</summary>
    public const string Transient = "transient";
}
