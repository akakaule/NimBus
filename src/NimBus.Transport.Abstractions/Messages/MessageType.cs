namespace NimBus.Core.Messages
{
    /// <summary>
    /// Wire-protocol classification of a NimBus message. Promoted into
    /// <c>NimBus.Transport.Abstractions</c> so transport implementations can route on
    /// it without taking a dependency on <c>NimBus.Core</c>; the namespace is
    /// preserved as <c>NimBus.Core.Messages</c> and a <c>[TypeForwardedTo]</c>
    /// declaration in the <c>NimBus.Core</c> assembly keeps existing
    /// <c>using NimBus.Core.Messages;</c> directives source-compatible.
    /// </summary>
    public enum MessageType
    {
        Unknown,

        EventRequest,

        ErrorResponse,
        ResolutionResponse,
        DeferralResponse,
        SkipResponse,

        ResubmissionRequest,
        SkipRequest,
        RetryRequest,

        ContinuationRequest,
        UnsupportedResponse,

        HeartbeatResponse,

        ProcessDeferredRequest
    }
}
