using System;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// A small, thread-safe token-bucket rate limiter driven by an injectable <see cref="TimeProvider"/>
    /// (so its replenishment is deterministically testable). The bucket starts full at
    /// <c>burstCapacity</c> and refills at <c>maxPerMinute</c> tokens per minute up to that capacity.
    /// </summary>
    internal sealed class TokenBucket
    {
        private readonly double _capacity;
        private readonly double _refillPerSecond;
        private readonly TimeProvider _timeProvider;
        private readonly object _gate = new();

        private double _tokens;
        private DateTimeOffset _lastRefillUtc;

        public TokenBucket(int maxPerMinute, int burstCapacity, TimeProvider timeProvider)
        {
            _capacity = burstCapacity;
            _refillPerSecond = maxPerMinute / 60.0;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _tokens = burstCapacity;
            _lastRefillUtc = _timeProvider.GetUtcNow();
        }

        /// <summary>
        /// Attempts to consume a single token. Returns true when a token was available (delivery
        /// allowed); false when the bucket is empty (the caller should suppress).
        /// </summary>
        public bool TryAcquire()
        {
            lock (_gate)
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }

                return false;
            }
        }

        private void Refill()
        {
            var now = _timeProvider.GetUtcNow();
            var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * _refillPerSecond));
            _lastRefillUtc = now;
        }
    }
}
