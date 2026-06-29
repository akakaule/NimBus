using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Agents.Internal;

namespace NimBus.Agents;

/// <summary>Dependency-injection registration for hosted NimBus agents.</summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a hosted agent: a typed REST gateway over <c>/api/agent/*</c> plus a background
    /// loop that drives <typeparamref name="THandler"/> (subscribe → receive → handle → publish →
    /// settle). The host must call <c>AddServiceDefaults()</c> for service discovery to resolve a
    /// <c>https+http://</c> <see cref="AgentOptions.BaseAddress"/>, or set an absolute URL.
    /// </summary>
    /// <typeparam name="THandler">The handler implementation.</typeparam>
    /// <typeparam name="TInput">The deserialized source-event payload type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="AgentOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddNimBusAgent<THandler, TInput>(this IServiceCollection services, Action<AgentOptions> configure)
        where THandler : class, IAgentHandler<TInput>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AgentOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.AgentId))
            throw new ArgumentException("AgentOptions.AgentId is required.", nameof(configure));
        if (string.IsNullOrWhiteSpace(options.SourceEventTypeId))
            throw new ArgumentException("AgentOptions.Subscribe(eventTypeId) must be called with a source event type.", nameof(configure));
        if (options.ReceiveWaitSeconds is < 0 or > 60)
            throw new ArgumentException("AgentOptions.ReceiveWaitSeconds must be between 0 and 60.", nameof(configure));

        services.AddSingleton(options);
        services.TryAddSingleton<THandler>();
        services.AddSingleton<IAgentHandler<TInput>>(sp => sp.GetRequiredService<THandler>());

        services.AddHttpClient<IAgentBusGateway, RestAgentBusGateway>(client =>
        {
            client.BaseAddress = new Uri(options.BaseAddress);
            client.DefaultRequestHeaders.Add("X-Agent-Id", options.AgentId);
        });

        services.AddHostedService<AgentLoopWorker<TInput>>();
        return services;
    }
}
