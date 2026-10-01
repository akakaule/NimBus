using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Implemented by an exception that carries a server-provided retry delay, such as the
/// <c>Retry-After</c> header of an HTTP 429 or 503 response.
/// </summary>
/// <remarks>
/// When a handler fails with an exception that implements this interface (directly or as an
/// inner exception), the scheduled retry waits for the longer of the retry policy's own delay and
/// <see cref="RetryAfter"/>, capped at <see cref="RetryPolicy.MaxDelay"/> when the policy sets one.
/// The retry policy still decides whether a retry happens at all.
/// </remarks>
public interface IRetryAfterHint
{
    /// <summary>
    /// Gets the delay the remote system asked for, or <c>null</c> when it gave none.
    /// </summary>
    TimeSpan? RetryAfter { get; }
}
