using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.ServiceBus.HealthChecks;

public static class ServiceBusHealthCheckExtensions
{
    public static IHealthChecksBuilder AddServiceBusHealthCheck(this IHealthChecksBuilder builder)
    {
        return builder.AddCheck<ServiceBusHealthCheck>(
            "servicebus",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
    }
}
