using System.Net.Http.Json;

namespace Crm.Adapter.Clients;

public interface IErrorModeClient
{
    Task<bool> IsErrorModeEnabledAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the demo outage flag from crm-api. Fail-open with a short timeout and
/// swallow-all: if the flag cannot be read, assume off — and, critically, never
/// let a TaskCanceledException escape into the handler path, where it would be
/// mistaken for a downstream timeout by anything inspecting exception chains.
/// Mirrors Erp.Adapter.Functions' ServiceModeClient.
/// </summary>
public sealed class ErrorModeClient(HttpClient http, ILogger<ErrorModeClient> logger) : IErrorModeClient
{
    public async Task<bool> IsErrorModeEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetFromJsonAsync<ModeResponse>("/api/admin/error-mode", cancellationToken);
            return response?.Enabled ?? false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read CRM error-mode flag — assuming disabled.");
            return false;
        }
    }

    private sealed record ModeResponse(bool Enabled, DateTimeOffset ChangedAt);
}
