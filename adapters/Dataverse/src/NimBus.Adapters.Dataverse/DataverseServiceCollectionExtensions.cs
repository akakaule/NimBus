using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Extensions;

namespace NimBus.Adapters.Dataverse;

/// <summary>Opt-in registration; the host supplies an IPublisherClient.</summary>
public static class DataverseServiceCollectionExtensions
{
    /// <summary>Add a single configured Dataverse ingress to the host.</summary>
    public static IServiceCollection AddDataverseAdapter(this IServiceCollection services, DataverseOptions options)
    {
        options.Validate();
        if (services.Any(d => d.ServiceType == typeof(DataverseOptions)))
            throw new InvalidOperationException("Only one Dataverse organization may be registered per host.");
        services.AddSingleton(options);
        services.TryAddSingleton<DataverseContextReader>();
        services.TryAddSingleton<DataverseIngress>();
        return services;
    }

    /// <summary>Compose the Dataverse adapter using the NimBus extension builder.</summary>
    public static INimBusBuilder AddDataverseAdapter(this INimBusBuilder builder, DataverseOptions options)
    {
        builder.Services.AddDataverseAdapter(options);
        return builder;
    }
}
