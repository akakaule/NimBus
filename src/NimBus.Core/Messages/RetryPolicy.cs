using System;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Defines how a failed message should be retried.
    /// </summary>
    public class RetryPolicy
    {
        /// <summary>
        /// Maximum number of retry attempts.
        /// </summary>
        public int MaxRetries { get; set; }

        /// <summary>
        /// The backoff strategy to use between retries.
        /// </summary>
        public BackoffStrategy Strategy { get; set; } = BackoffStrategy.Fixed;

        /// <summary>
        /// The base delay between retries.
        /// For Fixed: used as-is. For Linear: multiplied by attempt number. For Exponential: doubled per attempt.
        /// </summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Maximum delay cap (optional). If null, delay grows without bound.
        /// </summary>
        public TimeSpan? MaxDelay { get; set; }

        /// <summary>
        /// Calculates the delay for a given retry attempt (0-based).
        /// </summary>
        public TimeSpan GetDelay(int retryAttempt)
        {
            var delay = Strategy switch
            {
                BackoffStrategy.Fixed => BaseDelay,
                BackoffStrategy.Linear => TimeSpan.FromTicks(BaseDelay.Ticks * (retryAttempt + 1)),
                BackoffStrategy.Exponential => TimeSpan.FromTicks(BaseDelay.Ticks * (long)Math.Pow(2, retryAttempt)),
                _ => BaseDelay
            };

            if (MaxDelay.HasValue && delay > MaxDelay.Value)
                delay = MaxDelay.Value;

            return delay;
        }

        /// <summary>
        /// Gets the delay in minutes for a given retry attempt, for use with ISender.Send().
        /// </summary>
        public int GetDelayMinutes(int retryAttempt)
        {
            return (int)Math.Ceiling(GetDelay(retryAttempt).TotalMinutes);
        }
    }

    /// <summary>
    /// Backoff strategy for retry delays.
    /// </summary>
    public enum BackoffStrategy
    {
        /// <summary>
        /// Same delay between each retry.
        /// </summary>
        Fixed,

        /// <summary>
        /// Delay increases linearly: baseDelay * (attempt + 1).
        /// </summary>
        Linear,

        /// <summary>
        /// Delay doubles each retry: baseDelay * 2^attempt.
        /// </summary>
        Exponential
    }
}
