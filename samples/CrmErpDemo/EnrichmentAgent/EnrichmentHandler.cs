using CrmErpDemo.Contracts.Events;
using EnrichmentAgent.Classification;
using NimBus.Agents;

namespace EnrichmentAgent;

/// <summary>
/// Classifies a parked <c>CrmContactCreated</c> and publishes <c>crm.contact.enriched.v1</c>.
/// The NimBus.Agents SDK owns the receive → deserialize → publish → settle mechanics; this
/// handler is just the per-message logic.
/// </summary>
public sealed class EnrichmentHandler : IAgentHandler<CrmContactCreated>
{
    /// <summary>The enriched event type the agent defines and publishes.</summary>
    public const string EnrichedEventTypeId = "crm.contact.enriched.v1";

    /// <summary>JSON schema for <see cref="EnrichedEventTypeId"/> (registered on startup).</summary>
    public const string EnrichedSchema =
        "{\"type\":\"object\",\"required\":[\"industry\"],\"properties\":{\"contactId\":{\"type\":\"string\"},\"industry\":{\"type\":\"string\"},\"leadScore\":{\"type\":\"integer\"},\"rationale\":{\"type\":\"string\"}}}";

    private readonly IContactClassifier _classifier;

    public EnrichmentHandler(IContactClassifier classifier) => _classifier = classifier;

    /// <inheritdoc/>
    public async Task<AgentResult> HandleAsync(AgentContext<CrmContactCreated> context, CancellationToken cancellationToken)
    {
        var contact = context.Input;

        var enrichment = await _classifier.Classify(
            new ContactInput(
                ContactId: contact.ContactId.ToString(),
                FirstName: contact.FirstName,
                LastName: contact.LastName,
                Email: contact.Email,
                Phone: contact.Phone),
            cancellationToken);

        var enriched = new
        {
            contactId = contact.ContactId.ToString(),
            industry = enrichment.Industry,
            leadScore = enrichment.LeadScore,
            rationale = enrichment.Rationale,
        };

        // SessionId defaults to the received handoff's session (the SDK fills it in).
        return AgentResult.Complete(PublishSpec.FromObject(EnrichedEventTypeId, enriched));
    }
}
