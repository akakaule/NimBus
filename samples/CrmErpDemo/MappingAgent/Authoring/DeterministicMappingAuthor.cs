namespace MappingAgent.Authoring;

/// <summary>
/// A no-network, deterministic implementation of <see cref="IMappingAuthor"/>.
/// Emits a hard-coded correct JSONata for the known
/// <c>marketing.lead.created.v1</c> → <c>erp.customer.upsert.v1</c> shape.
/// For any other source/target pair it emits a generic field-copy transform.
/// Used in CI, smoke tests, and local dev without an Anthropic API key.
/// Mirrors <c>DeterministicContactClassifier</c> from the EnrichmentAgent.
/// </summary>
public sealed class DeterministicMappingAuthor : IMappingAuthor
{
    // ── Known shape: marketing.lead.created.v1 → erp.customer.upsert.v1 ──────

    private const string MarketingSourceId = "marketing.lead.created.v1";
    private const string ErpTargetId       = "erp.customer.upsert.v1";

    /// <summary>
    /// The canonical deterministic transform for the CrmErpDemo marketing→erp shape.
    /// Used by the MappingAgent smoke test and as the fallback when the LLM is absent.
    /// </summary>
    public const string MarketingToErpTransform =
        "{ \"customerId\": leadId, \"companyName\": company, \"email\": email }";

    /// <inheritdoc/>
    public Task<MappingProposal> Author(AuthoringInput input, CancellationToken cancellationToken = default)
    {
        if (input.SourceEventTypeId == MarketingSourceId && input.TargetEventTypeId == ErpTargetId)
        {
            return Task.FromResult(new MappingProposal(
                Transform: MarketingToErpTransform,
                Rationale:
                    "Deterministic mapping: leadId → customerId, company → companyName, email → email. " +
                    "Authored without an LLM (DeterministicMappingAuthor)."));
        }

        // Generic fallback for unknown shapes: copy every field verbatim.
        // The executor will validate the output against the target schema and park if it does not match.
        var genericTransform = "$$";
        return Task.FromResult(new MappingProposal(
            Transform: genericTransform,
            Rationale:
                $"Generic identity transform for {input.SourceEventTypeId} → {input.TargetEventTypeId}. " +
                "Review and update manually."));
    }
}
