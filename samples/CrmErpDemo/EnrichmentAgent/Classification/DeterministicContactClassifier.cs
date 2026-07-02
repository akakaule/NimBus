namespace EnrichmentAgent.Classification;

/// <summary>
/// A no-network, deterministic implementation of <see cref="IContactClassifier"/>.
/// Given the same input it always returns the same output — no external dependencies.
/// Used in CI, in-memory smoke tests, and any environment without an Anthropic API key.
/// </summary>
public sealed class DeterministicContactClassifier : IContactClassifier
{
    // Maps email domain suffixes / keywords to industry labels.
    private static readonly (string Keyword, string Industry)[] IndustryKeywords =
    [
        ("tech",       "Technology"),
        ("software",   "Technology"),
        ("dev",        "Technology"),
        ("cloud",      "Technology"),
        ("finance",    "Financial Services"),
        ("bank",       "Financial Services"),
        ("capital",    "Financial Services"),
        ("invest",     "Financial Services"),
        ("health",     "Healthcare"),
        ("medical",    "Healthcare"),
        ("pharma",     "Healthcare"),
        ("clinic",     "Healthcare"),
        ("retail",     "Retail"),
        ("shop",       "Retail"),
        ("store",      "Retail"),
        ("logistics",  "Logistics"),
        ("transport",  "Logistics"),
        ("shipping",   "Logistics"),
        ("edu",        "Education"),
        ("university", "Education"),
        ("school",     "Education"),
        ("media",      "Media & Entertainment"),
        ("news",       "Media & Entertainment"),
        ("studio",     "Media & Entertainment"),
    ];

    /// <inheritdoc/>
    public Task<ContactEnrichment> Classify(ContactInput contact, CancellationToken cancellationToken = default)
    {
        var industry = InferIndustry(contact);
        var leadScore = DeriveLeadScore(contact.ContactId);
        var rationale = $"Deterministic classification for contact {contact.ContactId}.";

        return Task.FromResult(new ContactEnrichment(industry, leadScore, rationale));
    }

    private static string InferIndustry(ContactInput contact)
    {
        // Build a searchable string from email domain + name fragments.
        var haystack = string.Concat(
            contact.Email ?? string.Empty,
            contact.FirstName ?? string.Empty,
            contact.LastName ?? string.Empty
        ).ToLowerInvariant();

        foreach (var (keyword, industry) in IndustryKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.Ordinal))
                return industry;
        }

        return "General Business";
    }

    /// <summary>
    /// Derives a stable lead score (0–100) from a hash of the ContactId string.
    /// Same ContactId always yields the same score.
    /// </summary>
    private static int DeriveLeadScore(string contactId)
    {
        // Use a simple but stable hash — not cryptographic, just deterministic.
        var hash = 0u;
        foreach (var ch in contactId)
            hash = hash * 31 + ch;

        return (int)(hash % 101); // [0, 100] inclusive
    }
}
