using D365Sales.Adapter.Clients;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;

namespace D365Sales.Adapter.Resilience;

/// <summary>
/// Stage-tuned timings (seconds, not the minutes production would use) bound from the
/// <c>Dynamics365:Resilience</c> configuration section.
/// </summary>
public sealed class D365ResilienceOptions
{
    /// <summary>First retry delay after a 429; doubles per attempt. A longer Retry-After wins.</summary>
    public int ThrottledBaseDelaySeconds { get; set; } = 5;

    public int ThrottledMaxDelaySeconds { get; set; } = 300;

    public int ThrottledMaxRetries { get; set; } = 5;

    /// <summary>First retry delay after a 503 or a lost connection; doubles per attempt.</summary>
    public int UnavailableBaseDelaySeconds { get; set; } = 15;

    public int UnavailableMaxDelaySeconds { get; set; } = 120;

    public int UnavailableMaxRetries { get; set; } = 4;

    public int CircuitMinimumThroughput { get; set; } = 5;

    public double CircuitFailurePercentageThreshold { get; set; } = 50;

    public int CircuitSamplingWindowSeconds { get; set; } = 30;

    public int CircuitBreakDurationSeconds { get; set; } = 20;

    public int CircuitHalfOpenProbeCount { get; set; } = 1;
}

/// <summary>How the adapter treats each kind of Dataverse failure.</summary>
public static class D365Resilience
{
    /// <summary>
    /// Throttling and outages are retried by NimBus with exponential backoff (broker-scheduled, so
    /// every attempt is audited); a throttled retry also waits at least Dataverse's Retry-After,
    /// because <see cref="DataverseThrottledException"/> implements <see cref="IRetryAfterHint"/>.
    /// There is deliberately no default policy: a rejection (4xx) is a data problem, so the message
    /// fails and waits for an operator to fix the data and resubmit.
    /// </summary>
    public static void ConfigureRetries(DefaultRetryPolicyProvider policies, D365ResilienceOptions options)
    {
        policies
            .AddExceptionRule<DataverseThrottledException>(new RetryPolicy
            {
                MaxRetries = options.ThrottledMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.ThrottledMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            })
            .AddExceptionRule<DataverseUnavailableException>(new RetryPolicy
            {
                MaxRetries = options.UnavailableMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.UnavailableMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            });
    }

    /// <summary>
    /// Pauses the adapter while Dataverse is down. Only outages count: throttling is paced by
    /// retries, and a rejected request is a data problem, not a sign Dataverse is unhealthy.
    /// </summary>
    public static void ConfigureCircuitBreaker(CircuitBreakerOptions circuit, D365ResilienceOptions options)
    {
        circuit.MinimumThroughput = options.CircuitMinimumThroughput;
        circuit.FailurePercentageThreshold = options.CircuitFailurePercentageThreshold;
        circuit.SamplingWindow = TimeSpan.FromSeconds(options.CircuitSamplingWindowSeconds);
        circuit.BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakDurationSeconds);
        circuit.HalfOpenProbeCount = options.CircuitHalfOpenProbeCount;
        circuit.Exclude<DataverseRequestRejectedException>();
        circuit.Exclude<DataverseThrottledException>();
    }
}
