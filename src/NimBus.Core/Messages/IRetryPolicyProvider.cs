namespace NimBus.Core.Messages
{
    /// <summary>
    /// Provides retry policies for failed messages.
    /// Implementations can source policies from configuration, code, or external stores.
    /// </summary>
    public interface IRetryPolicyProvider
    {
        /// <summary>
        /// Gets the retry policy for a given event type and failure context.
        /// Returns null if no retry should be attempted.
        /// </summary>
        /// <param name="eventTypeId">The event type that failed.</param>
        /// <param name="exceptionMessage">The exception message from the failure.</param>
        /// <param name="endpoint">The endpoint that failed (optional).</param>
        /// <returns>A retry policy, or null if no retry is configured.</returns>
        RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string endpoint = null);
    }
}
