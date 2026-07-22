namespace NimBus.MessageStore;

/// <summary>
/// Configuration for <see cref="CosmosInboxStore"/>.
/// </summary>
public sealed class CosmosInboxOptions
{
    /// <summary>
    /// Gets or sets the Cosmos DB database identifier. Defaults to <c>MessageDatabase</c>.
    /// </summary>
    public string DatabaseId { get; set; } = "MessageDatabase";

    /// <summary>
    /// Gets or sets the dedicated inbox container identifier. Defaults to <c>inbox</c>.
    /// </summary>
    public string ContainerId { get; set; } = "inbox";

    /// <summary>
    /// Gets or sets the maximum number of documents removed by one purge call.
    /// Defaults to 100 and cannot exceed 1,000.
    /// </summary>
    public int PurgeBatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets a value acknowledging that the account's effective consistency level is
    /// weaker than <c>Strong</c>. By default the store validates <c>Strong</c> at first use and
    /// fails fast otherwise: under Session (or weaker) consistency a duplicate check on another
    /// process can read a stale miss for a record a crashed consumer already committed, re-run
    /// the handler, and silently swallow the conflicting record — session tokens do not cross
    /// process boundaries, so no read strategy inside one client can close that window. Enable
    /// this only when a single consumer process serves the endpoint (no cross-replica
    /// redelivery) or the residual duplicate-side-effect risk is explicitly accepted.
    /// </summary>
    public bool AllowRelaxedConsistency { get; set; }

    internal void Validate()
    {
        ValidateResourceId(DatabaseId, nameof(DatabaseId));
        ValidateResourceId(ContainerId, nameof(ContainerId));
        if (PurgeBatchSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PurgeBatchSize),
                PurgeBatchSize,
                "PurgeBatchSize must be between 1 and 1,000.");
        }
    }

    private static void ValidateResourceId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.EndsWith(' ') ||
            value.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            throw new ArgumentException(
                "Cosmos DB resource identifiers must be 1-255 characters and cannot contain /, \\, ?, #, or end with a space.",
                parameterName);
        }
    }
}
