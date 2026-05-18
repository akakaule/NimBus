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
}
