using CrmErpDemo.Contracts.Events;
using EnrichmentAgent.Bus;
using EnrichmentAgent.Classification;
using Newtonsoft.Json;

namespace EnrichmentAgent;

/// <summary>
/// The agent loop: receive a parked <c>CrmContactCreated</c> from the NimBus agent
/// REST API, classify it, define <c>crm.contact.enriched.v1</c> (first run only),
/// publish the enriched event, then settle the original handoff. Per-message logic
/// lives in <see cref="ProcessNextAsync"/> so it is unit-testable against a fake
/// <see cref="IBusGateway"/> with no host or network.
/// </summary>
public sealed class AgentLoopWorker : BackgroundService
{
    /// <summary>The source event type the agent receives from the Agent Zone.</summary>
    public const string SourceEventTypeId = "CrmContactCreated";

    /// <summary>The enriched event type the agent defines and publishes.</summary>
    public const string EnrichedEventTypeId = "crm.contact.enriched.v1";

    /// <summary>JSON schema for <see cref="EnrichedEventTypeId"/>.</summary>
    public const string EnrichedSchema =
        "{\"type\":\"object\",\"required\":[\"industry\"],\"properties\":{\"contactId\":{\"type\":\"string\"},\"industry\":{\"type\":\"string\"},\"leadScore\":{\"type\":\"integer\"},\"rationale\":{\"type\":\"string\"}}}";

    // Small long-poll window: short enough to react to shutdown promptly, long
    // enough to avoid hammering the API when the Agent Zone is idle.
    private const int ReceiveWaitSeconds = 10;

    private readonly IBusGateway _bus;
    private readonly IContactClassifier _classifier;
    private readonly ILogger<AgentLoopWorker> _logger;

    // Guards DefineEventTypeAsync so the (idempotent) schema definition is attempted
    // only on the first processed message.
    private bool _schemaDefined;

    public AgentLoopWorker(IBusGateway bus, IContactClassifier classifier, ILogger<AgentLoopWorker> logger)
    {
        _bus = bus;
        _classifier = classifier;
        _logger = logger;
    }

    /// <summary>
    /// Runs one receive→classify→define→publish→settle cycle.
    /// Returns <c>true</c> if a message was processed, <c>false</c> if none was parked.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var msg = await _bus.ReceiveAsync(SourceEventTypeId, waitSeconds: ReceiveWaitSeconds, ct);
        if (msg is null)
            return false;

        _logger.LogInformation(
            "Received {EventTypeId} event {EventId} on session {SessionId}.",
            msg.EventTypeId, msg.Coordinates.EventId, msg.Coordinates.SessionId);

        var contact = JsonConvert.DeserializeObject<CrmContactCreated>(msg.Payload)
            ?? throw new InvalidOperationException($"{SourceEventTypeId} payload deserialized to null.");

        var input = new ContactInput(
            ContactId: contact.ContactId.ToString(),
            FirstName: contact.FirstName,
            LastName: contact.LastName,
            Email: contact.Email,
            Phone: contact.Phone);

        var enrichment = await _classifier.Classify(input, ct);
        _logger.LogInformation(
            "Classified contact {ContactId}: industry={Industry}, leadScore={LeadScore}.",
            input.ContactId, enrichment.Industry, enrichment.LeadScore);

        await EnsureSchemaDefinedAsync(ct);

        var enrichedJson = JsonConvert.SerializeObject(new
        {
            contactId = input.ContactId,
            industry = enrichment.Industry,
            leadScore = enrichment.LeadScore,
            rationale = enrichment.Rationale,
        });

        await _bus.PublishAsync(EnrichedEventTypeId, enrichedJson, sessionId: msg.Coordinates.SessionId, ct);
        _logger.LogInformation("Published {EventTypeId} for contact {ContactId}.", EnrichedEventTypeId, input.ContactId);

        await _bus.SettleAsync(msg.Coordinates, "complete", result: null, ct);
        _logger.LogInformation("Settled handoff {EventId} as complete.", msg.Coordinates.EventId);

        return true;
    }

    private async Task EnsureSchemaDefinedAsync(CancellationToken ct)
    {
        if (_schemaDefined)
            return;

        await _bus.DefineEventTypeAsync(EnrichedEventTypeId, EnrichedSchema, "Enriched CRM Contact", ct);
        _schemaDefined = true;
        _logger.LogInformation("Ensured event type {EventTypeId} is defined.", EnrichedEventTypeId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Register interest so receive is server-side filtered to CrmContactCreated.
        // Best-effort: if it fails, receive still works (it passes the type explicitly).
        try
        {
            await _bus.SubscribeAsync(SourceEventTypeId, stoppingToken);
            _logger.LogInformation("Subscribed to {EventTypeId}.", SourceEventTypeId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscribe to {EventTypeId} failed; continuing with explicit-type receive.", SourceEventTypeId);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent loop iteration failed; backing off before retry.");
                try { await Task.Delay(1000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
