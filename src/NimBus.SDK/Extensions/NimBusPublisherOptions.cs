namespace NimBus.SDK.Extensions
{
    /// <summary>
    /// Configuration options for a NimBus publisher registered via DI.
    /// </summary>
    public class NimBusPublisherOptions
    {
        /// <summary>
        /// The endpoint (topic name) to publish messages to.
        /// </summary>
        public string Endpoint { get; set; }
    }
}
