namespace NimBus.Core.Diagnostics;

/// <summary>
/// Values for the <see cref="MessagingAttributes.NimBusRetryAction"/> attribute on
/// <see cref="NimBusMeters.ResolverStoreRetry"/>: what the Resolver did with a message the
/// store could not persist.
/// </summary>
public static class RetryAction
{
    /// <summary>Re-sent as a scheduled message with the original MessageId after a backoff.</summary>
    public const string Rescheduled = "rescheduled";

    /// <summary>The delivery budget was exhausted; the message was dead-lettered.</summary>
    public const string DeadLettered = "dead_lettered";

    /// <summary>Scheduling was unavailable; the message was abandoned for broker redelivery.</summary>
    public const string Abandoned = "abandoned";
}
