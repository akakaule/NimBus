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

        /// <summary>
        /// When <c>false</c> (default), <c>AddNimBusSubscriber</c> also registers
        /// the worker-side <c>DeferredMessageProcessorHostedService</c> on the
        /// <c>deferredprocessor</c> trigger subscription. Set to <c>true</c> in
        /// hosts that own the trigger directly — most commonly Azure Functions
        /// adapters with their own <c>[ServiceBusTrigger]</c> function class.
        /// Leaving this <c>false</c> in a Functions host causes the
        /// auto-registered <c>BackgroundService</c> to compete with the function
        /// trigger for the same subscription.
        /// </summary>
        public bool DisableDeferredProcessorHostedService { get; set; }

        /// <summary>
        /// Name of the non-session trigger subscription the deferred-processor
        /// hosted service listens on. Default <c>"deferredprocessor"</c>.
        /// </summary>
        /// <remarks>
        /// Do not confuse with <c>NimBus.Core.Constants.DeferredSubscriptionName</c>
        /// (<c>"Deferred"</c>), which names the session-enabled parking
        /// subscription read by <c>IDeferredMessageProcessor</c> itself.
        /// </remarks>
        public string DeferredProcessorSubscriptionName { get; set; } = "deferredprocessor";
    }
}
