namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Configuration options for the Notifications extension.
    /// </summary>
    public class NotificationOptions
    {
        /// <summary>
        /// Whether to send notifications on message failures (handler exceptions). Default: true.
        /// </summary>
        public bool NotifyOnFailure { get; set; } = true;

        /// <summary>
        /// Whether to send notifications when messages are dead-lettered. Default: true.
        /// </summary>
        public bool NotifyOnDeadLetter { get; set; } = true;

        /// <summary>
        /// Whether to send notifications when messages are received (for monitoring). Default: false.
        /// </summary>
        public bool NotifyOnReceived { get; set; } = false;

        /// <summary>
        /// Whether to send notifications on successful completion. Default: false.
        /// </summary>
        public bool NotifyOnCompleted { get; set; } = false;
    }
}
