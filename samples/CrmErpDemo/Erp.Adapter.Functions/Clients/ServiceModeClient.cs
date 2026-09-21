using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Erp.Adapter.Functions.Clients;

public sealed class ServiceModeClient(HttpClient http, ILogger<ServiceModeClient> logger) : IServiceModeClient
{
    public async Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken)
    {
        var mode = await ReadAsync("/api/admin/service-mode", "service mode", cancellationToken);
        return mode?.Enabled ?? false;
    }

    public async Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken)
    {
        var mode = await ReadAsync("/api/admin/error-mode", "error mode", cancellationToken);
        return mode is null ? ErrorModeSnapshot.Disabled : new ErrorModeSnapshot(mode.Enabled, mode.Reason);
    }

    private async Task<ModeResponse?> ReadAsync(string path, string modeName, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetFromJsonAsync<ModeResponse>(path, cancellationToken);
        }
        catch (Exception ex)
        {
            // If the flag can't be read (e.g. erp-api unreachable), default to off — the
            // downstream call will fail on its own and surface the real cause.
            logger.LogDebug(ex, "Could not read {ModeName} flag — assuming disabled.", modeName);
            return null;
        }
    }

    // Reason is only present on the error-mode response; service-mode leaves it null.
    private sealed record ModeResponse(bool Enabled, string? Reason, DateTimeOffset ChangedAt);
}
