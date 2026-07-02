namespace MappingAgent.Bus;

/// <summary>
/// Seam over the NimBus agent REST surface for the Mapping Agent.
/// Mirrors <c>IBusGateway</c> from the EnrichmentAgent but covers schema catalog reads and
/// mapping-proposal writes. Transport-agnostic seam keeps <see cref="MappingAgentLoopWorker"/>
/// testable against a fake gateway. The v1 implementation is <see cref="RestMappingGateway"/>.
/// </summary>
public interface IMappingBusGateway
{
    /// <summary>Returns all registered schemas from <c>GET /api/agent/catalog</c>.</summary>
    Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Searches for recent source messages by <paramref name="eventTypeId"/> via
    /// <c>GET /api/messages/search?eventTypeId=…</c>. Returns raw JSON payloads.
    /// Returns an empty list when none are found rather than throwing.
    /// </summary>
    Task<IReadOnlyList<string>> GetSamplePayloadsAsync(string eventTypeId, int maxCount, CancellationToken ct = default);

    /// <summary>
    /// Returns the current mappings from <c>GET /api/agent/mappings</c> so the agent can avoid
    /// re-proposing over a mapping that is already Draft/Active/Paused. Returns an empty list
    /// when none exist rather than throwing.
    /// </summary>
    Task<IReadOnlyList<MappingSummary>> GetMappingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Submits a new Draft mapping proposal via <c>POST /api/agent/mappings</c>.
    /// Returns the created mapping id.
    /// </summary>
    Task<string> ProposeMappingAsync(
        string sourceEventTypeId,
        string targetEventTypeId,
        string transform,
        string rationale,
        string sourceSchemaHash,
        string? workedExamplesJson,
        CancellationToken ct = default);
}

/// <summary>A lightweight view of an existing mapping returned by <c>GET /api/agent/mappings</c>.</summary>
/// <param name="Id">Stable mapping id ("{source}->{target}").</param>
/// <param name="SourceEventTypeId">The source event type the mapping maps FROM.</param>
/// <param name="TargetEventTypeId">The target event type the mapping maps TO.</param>
/// <param name="State">Lifecycle state name (Draft, Active, Paused, Stale, Rejected).</param>
public sealed record MappingSummary(string Id, string SourceEventTypeId, string TargetEventTypeId, string State);

/// <summary>A schema catalog entry returned by <c>GET /api/agent/catalog</c>.</summary>
/// <param name="EventTypeId">The event type id (e.g. "marketing.lead.created.v1").</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="JsonSchema">Full JSON Schema string.</param>
public sealed record CatalogEntry(string EventTypeId, string Name, string JsonSchema);
