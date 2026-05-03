using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.MessageStore.HealthChecks;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddCosmosDbHealthCheck(this IHealthChecksBuilder builder)
    {
        return builder.AddCheck<CosmosDbHealthCheck>(
            "cosmosdb",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
    }

    public static IHealthChecksBuilder AddResolverLagCheck(
        this IHealthChecksBuilder builder,
        Action<ResolverLagHealthCheckOptions> configure = null)
    {
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }
        else
        {
            builder.Services.AddOptions<ResolverLagHealthCheckOptions>();
        }

        return builder.AddCheck<ResolverLagHealthCheck>(
            "resolver-lag",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
    }
}
