using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Erp.Adapter.Functions.Clients;

public sealed class ServiceModeClient(HttpClient http, ILogger<ServiceModeClient> logger) : IServiceModeClient
{
    public Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken) =>
        IsEnabledAsync("/api/admin/service-mode", "service mode", cancellationToken);

    public Task<bool> IsErrorModeEnabledAsync(CancellationToken cancellationToken) =>
        IsEnabledAsync("/api/admin/error-mode", "error mode", cancellationToken);

    private async Task<bool> IsEnabledAsync(string path, string modeName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetFromJsonAsync<ModeResponse>(path, cancellationToken);
            return response?.Enabled ?? false;
        }
        catch (Exception ex)
        {
            // If the flag can't be read (e.g. erp-api unreachable), default to off — the
            // downstream call will fail on its own and surface the real cause.
            logger.LogDebug(ex, "Could not read {ModeName} flag — assuming disabled.", modeName);
            return false;
        }
    }

    private sealed record ModeResponse(bool Enabled, DateTimeOffset ChangedAt);
}
