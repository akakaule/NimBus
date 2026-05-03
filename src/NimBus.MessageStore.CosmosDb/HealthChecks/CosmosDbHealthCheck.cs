using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.MessageStore.HealthChecks;

public class CosmosDbHealthCheck : IHealthCheck
{
    private readonly CosmosClient _client;

    public CosmosDbHealthCheck(CosmosClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.ReadAccountAsync();
            return HealthCheckResult.Healthy($"Cosmos DB is accessible. Account: {response.Id}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cosmos DB is not accessible.", ex);
        }
    }
}
