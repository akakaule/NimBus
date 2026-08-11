using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.ServiceBusEmulator.AspireHosting;

internal sealed class EmulatorEndpointHealthCheck(Func<EndpointReference> endpoint) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var current = endpoint();
        if (!current.IsAllocated)
        {
            return HealthCheckResult.Unhealthy("The emulator endpoint has not been allocated.");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(
                new Uri($"http://127.0.0.1:{current.Port}/health"),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"The emulator returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("The emulator readiness endpoint is unavailable.", exception);
        }
    }
}
