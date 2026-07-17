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
        public string TopicName { get; set; } = string.Empty;

        /// <summary>
        /// The subscription name within the topic.
        /// </summary>
        public string SubscriptionName { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of concurrent sessions to process. Default: 8.
        /// </summary>
        public int MaxConcurrentSessions { get; set; } = 8;

        /// <summary>
        /// Maximum duration for automatic lock renewal. Default: 5 minutes.
        /// </summary>
        public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long an idle session receiver waits before moving to another session. Default: 30 seconds.
        /// </summary>
        public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Number of consecutive recoverable processor errors before the session processor is recreated. Default: 5.
        /// </summary>
        public int RecoverableErrorRestartThreshold { get; set; } = 5;

        /// <summary>
        /// Window used when counting consecutive recoverable processor errors. Default: 2 minutes.
        /// </summary>
        public TimeSpan RecoverableErrorWindow { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Initial delay applied inside the processor error callback for recoverable infrastructure errors. Default: 1 second.
        /// </summary>
        public TimeSpan RecoverableErrorDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Maximum delay applied inside the processor error callback for recoverable infrastructure errors. Default: 30 seconds.
        /// </summary>
        public TimeSpan MaxRecoverableErrorDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Delay before creating a replacement processor after the current processor is stopped and disposed. Default: 5 seconds.
        /// </summary>
        public TimeSpan ProcessorRestartDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Maximum time recovery waits for a failed processor to stop and dispose
        /// before creating its replacement. Default: 30 seconds.
        /// </summary>
        public TimeSpan ProcessorShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}
