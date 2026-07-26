using System;

namespace NimBus.Core.Messages;

/// <summary>
/// The reply half of a request/reply exchange. Replies travel as raw session messages
/// addressed to the requester's <c>{endpoint}-reply</c> subscription — they are not
/// NimBus events: no <c>EventTypeId</c>, no <c>MessageType</c>, no Resolver audit row.
/// </summary>
public sealed class ReplyMessage
{
    /// <summary>Creates a reply addressed to the requesting endpoint's reply subscription.</summary>
    /// <param name="replyTo">The requesting endpoint's topic (the inbound message's <c>ReplyTo</c>).</param>
    /// <param name="replySessionId">The reply correlation session id (the inbound message's <c>ReplyToSessionId</c>).</param>
    /// <exception cref="ArgumentException">Thrown when either address component is null or empty.</exception>
    public ReplyMessage(string replyTo, string replySessionId)
    {
        if (string.IsNullOrEmpty(replyTo))
            throw new ArgumentException("ReplyTo must not be null or empty.", nameof(replyTo));
        if (string.IsNullOrEmpty(replySessionId))
            throw new ArgumentException("ReplySessionId must not be null or empty.", nameof(replySessionId));

        ReplyTo = replyTo;
        ReplySessionId = replySessionId;
    }

    /// <summary>The requesting endpoint's topic the reply is sent to.</summary>
    public string ReplyTo { get; }

    /// <summary>The session id correlating this reply to the awaiting request.</summary>
    public string ReplySessionId { get; }

    /// <summary>The request's correlation id, preserved for tracing.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The serialized <c>TResponse</c> JSON on success; null or empty on error replies.</summary>
    public string? PayloadJson { get; init; }

    /// <summary>True when the responder failed and this reply carries error details instead of a payload.</summary>
    public bool IsError { get; init; }

    /// <summary>The full CLR type name of the responder's exception; null on success.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The responder's exception message; null on success.</summary>
    public string? ErrorText { get; init; }
}
