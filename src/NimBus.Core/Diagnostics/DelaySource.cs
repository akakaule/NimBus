namespace NimBus.Core.Diagnostics;

/// <summary>
/// Values for the <see cref="MessagingAttributes.NimBusDelaySource"/> attribute on
/// <see cref="NimBusMeters.ResolverRetryDelay"/>: which side chose the applied retry delay.
/// </summary>
public static class DelaySource
{
    /// <summary>The store's RetryAfter hint was longer than the Resolver's backoff and won.</summary>
    public const string Provider = "provider";

    /// <summary>The Resolver's exponential backoff (5 s doubling to 5 min) was applied.</summary>
    public const string Backoff = "backoff";
}
