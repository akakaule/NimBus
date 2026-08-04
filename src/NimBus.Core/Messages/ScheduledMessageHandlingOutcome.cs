namespace NimBus.Core.Messages
{
    /// <summary>
    /// Neutral diagnostic signal a timeout handler reports after its durable
    /// workflow-state compare-and-set decides whether the timeout's effects were
    /// applied. Separate from <see cref="HandlerOutcome"/> and from Resolver
    /// status: a ResolutionResponse Completed means the timeout message was
    /// handled, not whether the business timeout won. If the handler never
    /// reports, NimBus records the receive only and does not invent a Fired
    /// result.
    /// </summary>
    public enum ScheduledMessageHandlingOutcome
    {
        /// <summary>The workflow-state compare-and-set accepted the timeout and its effects were applied.</summary>
        Fired = 0,

        /// <summary>The timeout arrived late (workflow completed, cancelled, superseded, or duplicate) and was ignored as a no-op.</summary>
        IgnoredLate = 1,
    }
}
