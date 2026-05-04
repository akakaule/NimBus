namespace NimBus.Core.Messages
{
    /// <summary>
    /// Outcome signalled by an event handler. <see cref="Default"/> means the
    /// handler completed normally and the subscriber will send a
    /// ResolutionResponse. <see cref="PendingHandoff"/> is signalled via
    /// <c>IEventHandlerContext.MarkPendingHandoff</c> when work has been
    /// handed off to a long-running external system; the subscriber sends a
    /// PendingHandoffResponse and blocks the session until the Manager
    /// settles it via CompleteHandoff or FailHandoff.
    /// </summary>
    public enum HandlerOutcome
    {
        Default = 0,
        PendingHandoff = 1
    }
}
