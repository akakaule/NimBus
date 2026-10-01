namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Options for <see cref="ServiceDefaultsExtensions.AddServiceDefaults"/>.
/// </summary>
public sealed class NimBusServiceDefaultsOptions
{
    /// <summary>
    /// Gets or sets whether every <c>IHttpClientFactory</c> client gets the standard HTTP resilience
    /// handler (retries, circuit breaker and timeouts). Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The handler retries inside a single NimBus delivery attempt. That hides failures from the
    /// NimBus retry rules and circuit breaker and can outlast the Service Bus message lock, so leave
    /// it off for an adapter's external-system client. Turn it on for hosts whose outbound calls are
    /// not part of message handling.
    /// </remarks>
    public bool UseStandardResilienceHandler { get; set; }
}
