namespace NimBus.Core.CloudEvents
{
    /// <summary>
    /// CloudEvents 1.0 content mode used when a NimBus publisher emits a CloudEvent.
    /// </summary>
    public enum CloudEventContentMode
    {
        /// <summary>
        /// Binary content mode: the message body is the raw serialized domain event
        /// and the CloudEvents context attributes ride as AMQP application properties
        /// (<c>cloudEvents:*</c>). The AMQP content-type is the data content type.
        /// </summary>
        Binary,

        /// <summary>
        /// Structured content mode: the entire CloudEvent (context + data) is
        /// serialized as a CloudEvents 1.0 JSON envelope in the message body and the
        /// AMQP content-type is <c>application/cloudevents+json</c>.
        /// </summary>
        StructuredJson,
    }
}
