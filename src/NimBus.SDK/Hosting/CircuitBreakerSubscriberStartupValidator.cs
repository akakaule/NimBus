using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NimBus.SDK.Hosting;

/// <summary>Holds the subscriber instance composed with the circuit recorder.</summary>
internal sealed class CircuitBreakerSubscriberComposition
{
    public ISubscriberClient? ComposedClient { get; set; }
}

/// <summary>Fails startup when a later custom subscriber bypasses the configured recorder.</summary>
internal sealed class CircuitBreakerSubscriberStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CircuitBreakerSubscriberComposition _composition;

    public CircuitBreakerSubscriberStartupValidator(
        IServiceProvider serviceProvider,
        CircuitBreakerSubscriberComposition composition)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var effectiveClient = _serviceProvider.GetRequiredService<ISubscriberClient>();
        if (!ReferenceEquals(_composition.ComposedClient, effectiveClient))
        {
            throw new InvalidOperationException(
                $"WithCircuitBreaker is configured, but the effective {nameof(ISubscriberClient)} is not the " +
                "subscriber NimBus composed with the circuit recorder. A custom registration added after " +
                "AddNimBusSubscriber wins Microsoft DI's last-registration rule, so the circuit would never " +
                $"observe handler outcomes. Remove the custom {nameof(ISubscriberClient)} registration, or " +
                "drop WithCircuitBreaker and integrate IEndpointCircuitBreaker into the custom composition.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
