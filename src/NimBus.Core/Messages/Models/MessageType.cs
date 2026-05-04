namespace NimBus.Core.Messages
{
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

        ProcessDeferredRequest,

        // Async-completion / PendingHandoff control flow.
        // PendingHandoffResponse: subscriber → Resolver, signals the message is
        // settled but the work is in flight on an external system.
        // HandoffCompletedRequest / HandoffFailedRequest: Manager → subscriber,
        // drive the Pending → Completed / Failed transition without re-invoking
        // the user handler.
        PendingHandoffResponse,
        HandoffCompletedRequest,
        HandoffFailedRequest
    }
}
