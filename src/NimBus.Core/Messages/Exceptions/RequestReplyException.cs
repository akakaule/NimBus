using System;

namespace NimBus.Core.Messages.Exceptions;

/// <summary>
/// Thrown by the request/reply requester when the responder answered with an error
/// reply — the responder's handler threw instead of producing a response. Carries
/// the responder-side exception identity so callers can fail fast (instead of
/// timing out) and log the remote cause.
/// </summary>
public class RequestReplyException : Exception
{
    /// <summary>Creates the exception from an error reply's details.</summary>
    /// <param name="errorType">The responder exception's full CLR type name (may be null).</param>
    /// <param name="errorText">The responder exception's message (may be null).</param>
    public RequestReplyException(string? errorType, string? errorText)
        : base($"The responder failed to handle the request: {errorType ?? "<unknown>"}: {errorText ?? "<no detail>"}")
    {
        ErrorType = errorType;
        ErrorText = errorText;
    }

    /// <summary>The full CLR type name of the responder-side exception, when provided.</summary>
    public string? ErrorType { get; }

    /// <summary>The responder-side exception message, when provided.</summary>
    public string? ErrorText { get; }
}
