using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Wire-contract constants for request/reply messages. A reply is a raw session
/// message: its only routable application property is <c>To = "{endpoint}-reply"</c>,
/// which matches the reply subscription's SQL rule and — deliberately — no event-type
/// forward rule, main-subscription rule, or Resolver rule. Replies therefore can
/// never be re-forwarded or audited as events.
/// </summary>
public static class ReplyConstants
{
    /// <summary>Suffix appended to the requesting endpoint id to form the reply subscription name.</summary>
    public const string ReplySubscriptionSuffix = "-reply";

    /// <summary>Application property carrying the reply outcome: <see cref="StatusSuccess"/> or <see cref="StatusError"/>.</summary>
    public const string ReplyStatusProperty = "ReplyStatus";

    /// <summary>Application property carrying the responder exception's full CLR type name on error replies.</summary>
    public const string ErrorTypeProperty = "ErrorType";

    /// <summary>Application property carrying the responder exception's message on error replies.</summary>
    public const string ErrorTextProperty = "ErrorText";

    /// <summary><see cref="ReplyStatusProperty"/> value for a successful reply.</summary>
    public const string StatusSuccess = "Success";

    /// <summary><see cref="ReplyStatusProperty"/> value for an error reply.</summary>
    public const string StatusError = "Error";

    /// <summary>
    /// Per-message time-to-live for replies. Orphaned replies (requester timed out,
    /// retries emitting extra error replies) self-clean after this window.
    /// </summary>
    public static readonly TimeSpan ReplyTimeToLive = TimeSpan.FromMinutes(5);

    /// <summary>Builds the reply subscription name for an endpoint.</summary>
    /// <param name="endpointId">The requesting endpoint id.</param>
    /// <returns>The subscription name, e.g. <c>CrmEndpoint-reply</c>.</returns>
    public static string ReplySubscriptionName(string endpointId) => $"{endpointId}{ReplySubscriptionSuffix}";
}
