using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace Marketing.Api;

/// <summary>
/// Seeds the <c>marketing.lead.created.v1</c> and <c>erp.customer.upsert.v1</c>
/// event-type schemas into the NimBus schema registry (via the agent REST API on
/// <c>nimbus-ops</c>) at startup. Idempotent: 409 Conflict means "already registered
/// with the same id" and is swallowed — any mismatch is ignored to keep the demo
/// self-healing across restarts.
/// </summary>
public sealed class SchemaSeeder : BackgroundService
{
    // Schema for the Marketing source event (spec 023).
    public const string MarketingLeadCreatedEventTypeId = "marketing.lead.created.v1";

    public const string MarketingLeadCreatedSchema =
        "{\"type\":\"object\",\"required\":[\"leadId\",\"firstName\",\"lastName\",\"company\",\"email\"]," +
        "\"properties\":{\"leadId\":{\"type\":\"string\"},\"firstName\":{\"type\":\"string\"}," +
        "\"lastName\":{\"type\":\"string\"},\"company\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}";

    // Schema for the canonical ERP target event that the AI mapping produces (spec 023).
    public const string ErpCustomerUpsertEventTypeId = "erp.customer.upsert.v1";

    public const string ErpCustomerUpsertSchema =
        "{\"type\":\"object\",\"required\":[\"customerId\",\"companyName\",\"email\"]," +
        "\"properties\":{\"customerId\":{\"type\":\"string\"},\"companyName\":{\"type\":\"string\"}," +
        "\"email\":{\"type\":\"string\"}}}";

    private readonly HttpClient _http;
    private readonly ILogger<SchemaSeeder> _logger;

    public SchemaSeeder(HttpClient http, ILogger<SchemaSeeder> logger)
    {
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry until nimbus-ops is reachable (it starts later in the Aspire graph).
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await SeedSchemaAsync(MarketingLeadCreatedEventTypeId, MarketingLeadCreatedSchema, "Marketing Lead Created", stoppingToken);
                await SeedSchemaAsync(ErpCustomerUpsertEventTypeId, ErpCustomerUpsertSchema, "ERP Customer Upsert", stoppingToken);
                _logger.LogInformation("Schema seeding complete: {Source} and {Target} registered.",
                    MarketingLeadCreatedEventTypeId, ErpCustomerUpsertEventTypeId);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schema seeding attempt {Attempt} failed; retrying in 5 s.", attempt);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task SeedSchemaAsync(string eventTypeId, string jsonSchema, string name, CancellationToken ct)
    {
        var body = JsonConvert.SerializeObject(new { eventTypeId, jsonSchema, name });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/api/agent/event-types", content, ct);

        // 409 = already registered (idempotent). Any other non-success = hard failure.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogInformation("Event type {EventTypeId} already registered (idempotent).", eventTypeId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Schema seed for '{eventTypeId}' failed: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
        }

        _logger.LogInformation("Event type {EventTypeId} registered as '{Name}'.", eventTypeId, name);
    }
}
