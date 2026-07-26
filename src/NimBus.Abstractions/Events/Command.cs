namespace NimBus.Core.Events
{
    /// <summary>
    /// Marker base class for command messages: events with imperative intent that must have
    /// exactly one consuming endpoint declared in the platform catalog.
    /// </summary>
    /// <remarks>
    /// A command travels the same pipeline and routing as any <see cref="Event"/> — it is
    /// routed by event type, ordered by session, and audited by the Resolver. The difference
    /// is contractual, not mechanical: a command instructs a single recipient to act, so the
    /// platform enforces that exactly one endpoint declares it consumed. Zero consumers would
    /// dead-letter every send; more than one would silently turn the instruction into a
    /// broadcast. <see cref="PlatformValidation"/> checks this rule at provisioning time.
    /// Prefer imperative names for commands (e.g. <c>PlaceCustomerOnCreditHold</c>) and
    /// past-tense names for events (e.g. <c>CustomerCreated</c>).
    /// </remarks>
    public abstract class Command : Event
    {
    }
}
