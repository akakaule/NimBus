namespace NimBus.SDK.Extensions
{
    /// <summary>
    /// Configuration options for a NimBus subscriber registered via DI.
    /// </summary>
    public class NimBusSubscriberOptions
    {
        /// <summary>
        /// The endpoint (topic name) for sending responses.
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Optional entity path (queue name or topic/subscription) for receiving deferred messages.
        /// </summary>
        public string EntityPath { get; set; }
    }
}
