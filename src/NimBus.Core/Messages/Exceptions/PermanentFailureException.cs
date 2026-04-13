using System;

namespace NimBus.Core.Messages.Exceptions;

/// <summary>
/// Thrown when an exception is classified as a permanent (unrecoverable) failure.
/// Caught by <see cref="MessageHandler"/> to dead-letter the message immediately
/// without consuming retry budget or blocking the session.
/// </summary>
public class PermanentFailureException : Exception
{
    public PermanentFailureException(Exception innerException)
        : base($"Permanent failure: {innerException?.Message}", innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
    }
}
