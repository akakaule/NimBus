using NimBus.Core.Messages;

namespace D365Sales.Adapter.Clients;

// The three ways a Dataverse call can fail, as distinct exception TYPES, so retry rules use
// AddExceptionRule<T>() and the circuit breaker Exclude<T>() instead of matching text. None of the
// names contains "Validation": the default permanent-failure classifier would dead-letter those if
// one were ever registered.

/// <summary>Base type of every Dataverse call failure.</summary>
public abstract class DataverseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 429 Too Many Requests: a Dataverse service protection limit was hit. Transient; NimBus retries it,
/// waiting at least the server's Retry-After. Excluded from the circuit breaker — throttling is
/// paced, not an outage.
/// </summary>
public sealed class DataverseThrottledException(string message, TimeSpan? retryAfter)
    : DataverseException(message), IRetryAfterHint
{
    /// <summary>The delay Dataverse asked for in its Retry-After header, if any.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 503/502/504/408 or no connection: Dataverse is unavailable. Transient; counted by the circuit
/// breaker (which pauses the adapter) and retried by NimBus.
/// </summary>
public sealed class DataverseUnavailableException(string message, Exception? innerException = null)
    : DataverseException(message, innerException);

/// <summary>
/// Any other 4xx: Dataverse refused the request (a missing record, a plug-in rejecting the change).
/// Not transient: the message fails, its session blocks, and an operator fixes the data and
/// resubmits. Excluded from the circuit breaker — a data problem is not an outage.
/// </summary>
public sealed class DataverseRequestRejectedException(string message, int statusCode, string? errorCode)
    : DataverseException(message)
{
    /// <summary>The HTTP status code Dataverse answered with.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The Dataverse error code from the response body, if any.</summary>
    public string? ErrorCode { get; } = errorCode;
}
