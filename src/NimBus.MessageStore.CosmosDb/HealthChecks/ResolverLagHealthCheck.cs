using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.HealthChecks;

public class ResolverLagHealthCheckOptions
{
    public TimeSpan HealthyThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan DegradedThreshold { get; set; } = TimeSpan.FromMinutes(15);
}

public class ResolverLagHealthCheck : IHealthCheck
{
    private readonly ICosmosDbClient _cosmosDbClient;
    private readonly ResolverLagHealthCheckOptions _options;

    public ResolverLagHealthCheck(ICosmosDbClient cosmosDbClient, IOptions<ResolverLagHealthCheckOptions> options)
    {
        _cosmosDbClient = cosmosDbClient ?? throw new ArgumentNullException(nameof(cosmosDbClient));
        _options = options?.Value ?? new ResolverLagHealthCheckOptions();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadatas = await _cosmosDbClient.GetMetadatasWithEnabledHeartbeat();

            if (metadatas == null || metadatas.Count == 0)
            {
                return HealthCheckResult.Healthy("No heartbeat-enabled endpoints configured.");
            }

            var now = DateTime.UtcNow;
            var unhealthyEndpoints = new List<string>();
            var degradedEndpoints = new List<string>();

            foreach (var metadata in metadatas)
            {
                if (metadata.Heartbeats == null || metadata.Heartbeats.Count == 0)
                {
                    unhealthyEndpoints.Add($"{metadata.EndpointId} (no heartbeats)");
                    continue;
                }

                var latestHeartbeat = metadata.Heartbeats
                    .OrderByDescending(h => h.ReceivedTime)
                    .First();

                var lag = now - latestHeartbeat.ReceivedTime;

                if (lag > _options.DegradedThreshold)
                {
                    unhealthyEndpoints.Add($"{metadata.EndpointId} (lag: {lag.TotalMinutes:F1}min)");
                }
                else if (lag > _options.HealthyThreshold)
                {
                    degradedEndpoints.Add($"{metadata.EndpointId} (lag: {lag.TotalMinutes:F1}min)");
                }
            }

            if (unhealthyEndpoints.Count > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Resolver lag exceeds {_options.DegradedThreshold.TotalMinutes}min threshold: {string.Join(", ", unhealthyEndpoints)}");
            }

            if (degradedEndpoints.Count > 0)
            {
                return HealthCheckResult.Degraded(
                    $"Resolver lag exceeds {_options.HealthyThreshold.TotalMinutes}min threshold: {string.Join(", ", degradedEndpoints)}");
            }

            return HealthCheckResult.Healthy("All heartbeat-enabled endpoints are within threshold.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Failed to check resolver lag.", ex);
        }
    }
}
