namespace NimBus.Core.Endpoints
{
    /// <summary>
    /// A code-first declaration that a dynamically-typed event (identified only by its string
    /// <see cref="EventTypeId"/>, with no compiled IEvent class) published on
    /// <see cref="SourceEndpoint"/> must be forwarded to <see cref="TargetEndpoint"/>.
    /// Used by topology provisioning to create the forward subscription + EventTypeId rule that the
    /// compiled-event forward loop cannot derive (spec 022 D5).
    /// </summary>
    public sealed record DynamicForward(string SourceEndpoint, string EventTypeId, string TargetEndpoint);
}
