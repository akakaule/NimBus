using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.ServiceBus.HealthChecks;

public class ServiceBusHealthCheck : IHealthCheck
{
    private readonly ServiceBusClient _client;

    public ServiceBusHealthCheck(ServiceBusClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_client.IsClosed)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Service Bus client is closed."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Service Bus connection is active. FQNS: {_client.FullyQualifiedNamespace}"));
    }
}
