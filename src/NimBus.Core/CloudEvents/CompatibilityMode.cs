namespace NimBus.Core.CloudEvents
{
    /// <summary>
    /// Wire-compatibility mode for a NimBus publisher or subscriber.
    /// </summary>
    public enum CompatibilityMode
    {
        /// <summary>Native NimBus wire format (default; unchanged behavior).</summary>
        NimBusNative,

        /// <summary>CloudEvents 1.0 binary content mode.</summary>
        CloudEventsBinary,

        /// <summary>CloudEvents 1.0 structured JSON content mode.</summary>
        CloudEventsStructuredJson,

        /// <summary>
        /// Subscriber-only: transparently handle both native NimBus messages and
        /// CloudEvents messages on the same subscription, detected per message.
        /// </summary>
        AutoDetect,
    }
}
