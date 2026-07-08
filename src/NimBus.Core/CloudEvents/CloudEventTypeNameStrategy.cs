namespace NimBus.Core.CloudEvents
{
    /// <summary>
    /// Strategy for deriving the CloudEvents <c>type</c> attribute from a NimBus event.
    /// </summary>
    public enum CloudEventTypeNameStrategy
    {
        /// <summary>
        /// Use the unqualified CLR class name (matches NimBus's native
        /// <c>EventTypeId</c>). This is the default so a NimBus round-trip's
        /// <c>type</c> equals the dispatch key.
        /// </summary>
        UnqualifiedName,

        /// <summary>Use the fully-qualified CLR type name (namespace + class).</summary>
        FullName,
    }
}
