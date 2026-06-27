using System;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Router-wide options: the optional global rate limit and the deduplication window.
    /// </summary>
    public sealed class NotificationRouterOptions
    {
        /// <summary>
        /// Sustained delivery rate (notifications per minute) once the burst is exhausted.
        /// <c>null</c> disables rate limiting (severity routing and dedup still apply).
        /// </summary>
        public int? MaxPerMinute { get; set; }

        /// <summary>
        /// Maximum burst of notifications delivered before the rate limit engages.
        /// <c>null</c> disables rate limiting.
        /// </summary>
        public int? BurstCapacity { get; set; }

        /// <summary>
        /// Window within which a repeated <c>(EventId, Severity)</c> is collapsed to a single
        /// delivery. Default: 5 minutes.
        /// </summary>
        public TimeSpan DedupWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>True when a global rate limit is configured.</summary>
        public bool RateLimitEnabled => MaxPerMinute is > 0 && BurstCapacity is > 0;
    }
}
