using System.Net.Http.Json;

namespace Erp.Adapter.Functions.HandoffMode;

public sealed class HandoffJobRegistration(HttpClient http) : IHandoffJobRegistration
{
    public async Task RegisterAsync(HandoffJob job, CancellationToken cancellationToken)
    {
        var response = await http.PostAsJsonAsync("/api/internal/handoff-jobs", job, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
