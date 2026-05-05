using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Erp.Adapter.Functions.HandoffMode;

public sealed class HandoffModeClient(HttpClient http, ILogger<HandoffModeClient> logger) : IHandoffModeClient
{
    private static readonly HandoffModeSnapshot Disabled = new(false, 0, 0.0);

    public async Task<HandoffModeSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetFromJsonAsync<ModeResponse>("/api/admin/handoff-mode", cancellationToken);
            if (response is null)
                return Disabled;
            return new HandoffModeSnapshot(response.Enabled, response.DurationSeconds, response.FailureRate);
        }
        catch (Exception ex)
        {
            // If the flag can't be read (e.g. erp-api unreachable), default to off — the
            // synchronous upsert path will run instead and surface any real error.
            logger.LogDebug(ex, "Could not read handoff mode flag — assuming disabled.");
            return Disabled;
        }
    }

    private sealed record ModeResponse(bool Enabled, int DurationSeconds, double FailureRate, DateTimeOffset ChangedAt);
}
