using NimBus.Core.Messages;

namespace BusinessCentral.Adapter.Clients;

// The three ways a Business Central call can fail, as distinct exception TYPES, so retry rules use
// AddExceptionRule<T>() and the circuit breaker Exclude<T>() instead of matching text such as "429",
// which could also match a line number in a stack trace.
// None of the names contains "Validation": the default permanent-failure classifier would
// dead-letter those if one were ever registered.

/// <summary>Base type of every Business Central call failure.</summary>
public abstract class BusinessCentralException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 429 Too Many Requests: the per-user rate limit is exceeded. Transient; NimBus retries it with
/// exponential backoff, waiting at least BC's Retry-After. Excluded from the circuit breaker —
/// throttling is paced, not an outage.
/// </summary>
public sealed class BcThrottledException(string message, TimeSpan? retryAfter)
    : BusinessCentralException(message), IRetryAfterHint
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 503/502/504/408 or no connection: Business Central is unavailable, typically an update window.
/// Transient; counted by the circuit breaker (which pauses the adapter) and retried by NimBus after
/// the window.
/// </summary>
public sealed class BcUnavailableException(string message, Exception? innerException = null)
    : BusinessCentralException(message, innerException);

/// <summary>
/// Any other 4xx: Business Central refused the request on business grounds (missing salesperson,
/// blocked item). Not transient: the message fails, its session blocks, and an operator fixes the
/// data in BC and resubmits. Excluded from the circuit breaker — a data problem is not an outage.
/// </summary>
public sealed class BcRequestRejectedException(string message, int statusCode, string? errorCode)
    : BusinessCentralException(message)
{
    public int StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;
}
