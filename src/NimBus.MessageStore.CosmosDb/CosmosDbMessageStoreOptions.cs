using System;

namespace NimBus.MessageStore;

/// <summary>
/// Options for the Cosmos DB message store. Bind from the <c>NimBus:Cosmos</c>
/// configuration section (environment form: <c>NimBus__Cosmos__UnresolvedRetentionDays</c>).
/// </summary>
public sealed class CosmosDbMessageStoreOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "NimBus:Cosmos";

    /// <summary>Sentinel meaning "never expire" — the default, and the behaviour before this option existed.</summary>
    public const int UnlimitedRetentionDays = -1;

    /// <summary>
    /// Largest supported retention, in whole days (one year). This is a deliberate product
    /// bound, not a technical ceiling: Cosmos stores <c>ttl</c> as integer seconds and would
    /// accept up to 24 855 days. One year was chosen so that the longest retention an operator
    /// can configure for a payload-bearing unresolved row does not exceed the retention of the
    /// audit documents that describe it. Raising it is a product decision, not a bug fix.
    /// </summary>
    public const int MaxRetentionDays = 365;

    private const int SecondsPerDay = 86_400;

    /// <summary>
    /// Retention, in whole days, stamped as the document <c>ttl</c> on non-terminal tracking rows
    /// (Pending, Failed, Deferred, DeadLettered, Unsupported). Valid values are
    /// <see cref="UnlimitedRetentionDays"/> (the default, no expiry) or 1 to
    /// <see cref="MaxRetentionDays"/>. Expiry deletes the whole tracking document, including its
    /// audit metadata and the ability to resubmit that event.
    /// </summary>
    public int UnresolvedRetentionDays { get; set; } = UnlimitedRetentionDays;

    /// <summary>
    /// Throws when <see cref="UnresolvedRetentionDays"/> is outside the supported range.
    /// Public because callers outside this assembly — the <c>nb</c> CLI, which stamps the same
    /// retention when it rewrites a tracking document — must be able to fail fast on a bad value
    /// before they open a Cosmos connection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="UnresolvedRetentionDays"/> is not <see cref="UnlimitedRetentionDays"/> and not
    /// between 1 and <see cref="MaxRetentionDays"/>.
    /// </exception>
    public void Validate()
    {
        if (!IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(UnresolvedRetentionDays),
                UnresolvedRetentionDays,
                DescribeInvalid(UnresolvedRetentionDays));
        }
    }

    // One message builder so the constructor throw, the options-validation failure and the CLI
    // parse failure are word-identical; both the option name and the offending value are required.
    internal static string DescribeInvalid(int value) =>
        $"{nameof(CosmosDbMessageStoreOptions)}.{nameof(UnresolvedRetentionDays)} must be "
        + $"{UnlimitedRetentionDays} (unlimited) or between 1 and {MaxRetentionDays} days; was {value}.";

    internal bool IsValid =>
        UnresolvedRetentionDays == UnlimitedRetentionDays
        || (UnresolvedRetentionDays >= 1 && UnresolvedRetentionDays <= MaxRetentionDays);

    // MaxRetentionDays caps the product at 31_536_000, three orders of magnitude below
    // int.MaxValue, and every caller runs Validate() first — so this cannot overflow. Keeping the
    // bound and the multiplication in one type is what stops the two from drifting apart.
    internal int ResolveUnresolvedTimeToLiveSeconds() =>
        UnresolvedRetentionDays == UnlimitedRetentionDays
            ? UnlimitedRetentionDays
            : UnresolvedRetentionDays * SecondsPerDay;
}
