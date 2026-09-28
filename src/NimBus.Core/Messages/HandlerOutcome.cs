namespace NimBus.Core.Messages;

/// <summary>
/// Outcome of dispatching a message to its event handler. <see cref="Default"/> means the
/// handler completed normally and the subscriber will send a
/// ResolutionResponse. <see cref="PendingHandoff"/> is signalled via
/// <c>IEventHandlerContext.MarkPendingHandoff</c> when work has been
/// handed off to a long-running external system; the subscriber sends a
/// PendingHandoffResponse and blocks the session until the Manager
/// settles it via IHandoffClient.CompleteAsync or FailAsync.
/// </summary>
public enum HandlerOutcome
{
    Default = 0,
    PendingHandoff = 1,

    /// <summary>
    /// The inbox identified a previously processed message and skipped handler dispatch.
    /// </summary>
    DuplicateDetected = 2,

    /// <summary>
    /// The message was answered without dispatching a handler: a control message that never
    /// invokes one (a skip, a handoff settlement, a legacy continuation), a RetryRequest for
    /// an event that no longer blocks its session, or an event type without a registered
    /// handler. Set by <c>StrictMessageHandler</c>.
    /// </summary>
    NotDispatched = 3
}
