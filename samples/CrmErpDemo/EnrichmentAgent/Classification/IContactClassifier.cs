namespace EnrichmentAgent.Classification;

/// <summary>
/// Classifies a CRM contact into industry, lead score, and rationale.
/// </summary>
public interface IContactClassifier
{
    /// <summary>
    /// Classifies the given contact and returns enrichment data.
    /// </summary>
    Task<ContactEnrichment> Classify(ContactInput contact, CancellationToken cancellationToken = default);
}

/// <summary>
/// Input derived from <c>CrmContactCreated</c> for classification purposes.
/// </summary>
/// <param name="ContactId">The unique identifier of the CRM contact (maps to CrmContactCreated.ContactId).</param>
/// <param name="FirstName">Contact first name (maps to CrmContactCreated.FirstName).</param>
/// <param name="LastName">Contact last name (maps to CrmContactCreated.LastName).</param>
/// <param name="Email">Contact email address (maps to CrmContactCreated.Email).</param>
/// <param name="Phone">Contact phone number (maps to CrmContactCreated.Phone).</param>
public sealed record ContactInput(
    string ContactId,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone);

/// <summary>
/// Enrichment data produced by the classifier. Must be valid against the
/// <c>crm.contact.enriched.v1</c> JSON schema:
/// <c>{"type":"object","required":["industry"],"properties":{"contactId":{"type":"string"},"industry":{"type":"string"},"leadScore":{"type":"integer"},"rationale":{"type":"string"}}}</c>
/// </summary>
/// <param name="Industry">Inferred industry vertical (required). Must be non-empty.</param>
/// <param name="LeadScore">Lead score 0–100 (integer). Higher is hotter.</param>
/// <param name="Rationale">Optional free-text explanation of the classification.</param>
public sealed record ContactEnrichment(string Industry, int LeadScore, string? Rationale);
