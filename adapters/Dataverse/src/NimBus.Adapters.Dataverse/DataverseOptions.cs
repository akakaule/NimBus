namespace NimBus.Adapters.Dataverse;

/// <summary>Explicit source and projection configuration for one adapter instance.</summary>
public sealed class DataverseOptions
{
    /// <summary>Only this organization may publish through this instance.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Publisher endpoint provisioned in the NimBus catalog.</summary>
    public string PublisherEndpoint { get; set; } = "DataverseEndpoint";

    /// <summary>Allowed tables and selected columns. No wildcard projection.</summary>
    public Dictionary<string, string[]> Tables { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Optional required pre-image alias for Update/Delete.</summary>
    public string? PreImageAlias { get; set; }

    /// <summary>Optional required post-image alias for Create/Update.</summary>
    public string? PostImageAlias { get; set; }

    /// <summary>Maximum UTF-8 input size; default matches the source context threshold.</summary>
    public int MaxBodyBytes { get; set; } = 192 * 1024;

    /// <summary>Reject incomplete or unsafe configuration before accepting messages.</summary>
    public void Validate()
    {
        if (OrganizationId == Guid.Empty || string.IsNullOrWhiteSpace(PublisherEndpoint) ||
            MaxBodyBytes is < 1024 or > 192 * 1024 || Tables.Count == 0 ||
            Tables.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Any(string.IsNullOrWhiteSpace)))
        {
            throw new ArgumentException("Configure OrganizationId, PublisherEndpoint, bounded MaxBodyBytes and an explicit table/column allowlist.");
        }
    }
}
