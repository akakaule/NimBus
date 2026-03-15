using System;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Configuration options for the NimBus receiver hosted service.
    /// </summary>
    public class NimBusReceiverOptions
    {
        /// <summary>
        /// The Service Bus topic name to receive messages from.
        /// </summary>
        public string TopicName { get; set; }

        /// <summary>
        /// The subscription name within the topic.
        /// </summary>
        public string SubscriptionName { get; set; }

        /// <summary>
        /// Maximum number of concurrent sessions to process. Default: 1.
        /// </summary>
        public int MaxConcurrentSessions { get; set; } = 1;

        /// <summary>
        /// Maximum duration for automatic lock renewal. Default: 5 minutes.
        /// </summary>
        public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
    }
}
