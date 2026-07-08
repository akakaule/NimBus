namespace NimBus.Core.CloudEvents
{
    /// <summary>
    /// Transient carrier attached to a NimBus <c>Message</c> when the publisher has
    /// enabled CloudEvents. It tells the transport binding to emit the message as a
    /// CloudEvent in the configured content mode. Absent (null) on native messages,
    /// so the native wire format is byte-identical when CloudEvents is not used.
    /// </summary>
    /// <param name="CloudEvent">The CloudEvent context to emit.</param>
    /// <param name="ContentMode">Binary or structured JSON content mode.</param>
    public sealed record CloudEventPublishContext(CloudEvent CloudEvent, CloudEventContentMode ContentMode);
}
