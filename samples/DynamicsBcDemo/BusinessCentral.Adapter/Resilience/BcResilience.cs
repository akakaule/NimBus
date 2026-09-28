using BusinessCentral.Adapter.Clients;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;

namespace BusinessCentral.Adapter.Resilience;

/// <summary>
/// Stage-tuned timings (seconds, not the minutes production would use) bound from the
/// <c>BusinessCentral:Resilience</c> configuration section.
/// </summary>
public sealed class BcResilienceOptions
{
    /// <summary>First retry delay after a 429; doubles per attempt.</summary>
    public int ThrottledBaseDelaySeconds { get; set; } = 5;

    public int ThrottledMaxDelaySeconds { get; set; } = 60;

    public int ThrottledMaxRetries { get; set; } = 5;

    /// <summary>
    /// First retry delay after a 503. Longer than the demo's update window on purpose: the first
    /// retry lands after BC is back, so it succeeds and the scene ends on its own. A retry inside
    /// the window would fail again and count against the circuit breaker.
    /// </summary>
    public int UnavailableBaseDelaySeconds { get; set; } = 30;

    public int UnavailableMaxDelaySeconds { get; set; } = 120;

    public int UnavailableMaxRetries { get; set; } = 4;

    public int CircuitMinimumThroughput { get; set; } = 3;

    public double CircuitFailurePercentageThreshold { get; set; } = 50;

    public int CircuitSamplingWindowSeconds { get; set; } = 60;

    public int CircuitBreakDurationSeconds { get; set; } = 10;

    public int CircuitHalfOpenProbeCount { get; set; } = 1;
}

/// <summary>How the adapter treats each kind of Business Central failure.</summary>
public static class BcResilience
{
    /// <summary>
    /// Throttling and outages are retried by NimBus with exponential backoff (broker-scheduled, so
    /// every attempt is audited). There is deliberately no default policy: a rejection (4xx) is a
    /// data problem, so the message fails and waits for an operator to fix BC and resubmit.
    /// Rules match on the exception type name, which is part of the text NimBus matches.
    /// </summary>
    public static void ConfigureRetries(DefaultRetryPolicyProvider policies, BcResilienceOptions options)
    {
        policies
            .AddExceptionRule(nameof(BcThrottledException), new RetryPolicy
            {
                MaxRetries = options.ThrottledMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.ThrottledMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            })
            .AddExceptionRule(nameof(BcUnavailableException), new RetryPolicy
            {
                MaxRetries = options.UnavailableMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.UnavailableMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            });
    }

    /// <summary>
    /// Pauses the adapter while Business Central is down. Only outages count: throttling is paced
    /// by retries, and a rejected request is a data problem, not a sign BC is unhealthy.
    /// </summary>
    public static void ConfigureCircuitBreaker(CircuitBreakerOptions circuit, BcResilienceOptions options)
    {
        circuit.MinimumThroughput = options.CircuitMinimumThroughput;
        circuit.FailurePercentageThreshold = options.CircuitFailurePercentageThreshold;
        circuit.SamplingWindow = TimeSpan.FromSeconds(options.CircuitSamplingWindowSeconds);
        circuit.BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakDurationSeconds);
        circuit.HalfOpenProbeCount = options.CircuitHalfOpenProbeCount;
        circuit.Exclude<BcRequestRejectedException>();
        circuit.Exclude<BcThrottledException>();
    }
}
