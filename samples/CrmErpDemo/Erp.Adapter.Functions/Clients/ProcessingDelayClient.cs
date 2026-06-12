using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Erp.Adapter.Functions.Clients;

public sealed class ProcessingDelayClient(HttpClient http, ILogger<ProcessingDelayClient> logger) : IProcessingDelayClient
{
    public async Task<int> GetProcessingDelayMsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetFromJsonAsync<DelayResponse>("/api/admin/processing-delay", cancellationToken);
            return response is { Enabled: true } ? Math.Max(0, response.DelayMs) : 0;
        }
        catch (Exception ex)
        {
            // If the setting can't be read (e.g. erp-api unreachable), apply no delay —
            // the message proceeds normally rather than stalling on a config lookup.
            logger.LogDebug(ex, "Could not read processing-delay setting — applying no delay.");
            return 0;
        }
    }

    private sealed record DelayResponse(bool Enabled, int DelayMs, DateTimeOffset ChangedAt);
}
