using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB-backed implementation of <see cref="ISessionStateStore"/>. State is
/// stored as documents in a per-database <c>session-states</c> container,
/// partitioned by <c>endpointId</c>. The document <c>id</c> is the session id —
/// (endpointId, sessionId) is the natural key.
/// </summary>
public sealed class CosmosDbSessionStateStore : ISessionStateStore
{
    private readonly ICosmosClientAdapter _cosmosClient;
    private const string DatabaseId = "MessageDatabase";
    private const string ContainerId = "session-states";

    public CosmosDbSessionStateStore(CosmosClient cosmosClient)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _cosmosClient = new CosmosClientAdapter(cosmosClient);
    }

    internal CosmosDbSessionStateStore(ICosmosClientAdapter cosmosClient)
    {
        _cosmosClient = cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient));
    }

    private async Task<ICosmosContainerAdapter> GetContainer()
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await db.CreateContainerIfNotExistsAsync(ContainerId, "/endpointId").ConfigureAwait(false);
    }

    private static string DocumentId(string sessionId) => sessionId;

    private async Task<SessionStateDocument?> Read(string endpointId, string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = await GetContainer().ConfigureAwait(false);
        try
        {
            var response = await container.ReadItemAsync<SessionStateDocument>(
                DocumentId(sessionId), new PartitionKey(endpointId)).ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<SessionStateDocument> ReadOrCreate(string endpointId, string sessionId, CancellationToken cancellationToken)
    {
        return await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false)
            ?? new SessionStateDocument
            {
                Id = DocumentId(sessionId),
                EndpointId = endpointId,
                SessionId = sessionId,
            };
    }

    private async Task Upsert(SessionStateDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        document.UpdatedAtUtc = DateTime.UtcNow;
        var container = await GetContainer().ConfigureAwait(false);
        await container.UpsertItemAsync(document, new PartitionKey(document.EndpointId)).ConfigureAwait(false);
    }

    public async Task BlockSession(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var doc = await ReadOrCreate(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        doc.BlockedByEventId = eventId;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnblockSession(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        if (doc == null) return;
        doc.BlockedByEventId = null;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSessionBlocked(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        if (doc == null) return false;
        return !string.IsNullOrEmpty(doc.BlockedByEventId) || doc.DeferredCount > 0;
    }

    public async Task<bool> IsSessionBlockedByThis(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc != null
            && !string.IsNullOrEmpty(doc.BlockedByEventId)
            && string.Equals(doc.BlockedByEventId, eventId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsSessionBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc != null && !string.IsNullOrEmpty(doc.BlockedByEventId);
    }

    public async Task<string> GetBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc?.BlockedByEventId ?? string.Empty;
    }

    public async Task<int> GetNextDeferralSequenceAndIncrement(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await ReadOrCreate(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        var sequence = doc.NextDeferralSequence;
        doc.NextDeferralSequence = sequence + 1;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    public async Task IncrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await ReadOrCreate(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        doc.DeferredCount += 1;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }

    public async Task DecrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        if (doc == null || doc.DeferredCount <= 0) return;
        doc.DeferredCount -= 1;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc?.DeferredCount ?? 0;
    }

    public async Task<bool> HasDeferredMessages(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc != null && doc.DeferredCount > 0;
    }

    public async Task ResetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        if (doc == null) return;
        doc.DeferredCount = 0;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        // -1 sentinel: no replay has occurred for this session (either no doc,
        // or doc exists but LastReplayedSequence has never been written).
        return doc?.LastReplayedSequence ?? -1;
    }

    /// <summary>
    /// Forward-only conditional advance using Cosmos ETag-based optimistic
    /// concurrency. Single-attempt: returns <c>true</c> on success, <c>false</c>
    /// on 412 Precondition Failed (concurrent replayer raced ahead) or 409
    /// Conflict (concurrent first-time create). The design's serialization
    /// point is "loser falls back and re-reads"; we do NOT retry the OCC here.
    /// </summary>
    public async Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default)
    {
        // Forward-only invariant: never let the checkpoint go backwards.
        if (newValue <= expectedCurrent) return false;

        cancellationToken.ThrowIfCancellationRequested();
        var container = await GetContainer().ConfigureAwait(false);

        // Read with the ETag accessible via ItemResponse so we can pass it back
        // on the conditional Replace.
        ItemResponse<SessionStateDocument>? readResponse = null;
        try
        {
            readResponse = await container
                .ReadItemAsync<SessionStateDocument>(DocumentId(sessionId), new PartitionKey(endpointId))
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // First-time advance — create the document with IfNoneMatch:* so
            // a concurrent first-time create races and only one wins.
            if (expectedCurrent != -1) return false;
            var fresh = new SessionStateDocument
            {
                Id = DocumentId(sessionId),
                EndpointId = endpointId,
                SessionId = sessionId,
                LastReplayedSequence = newValue,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            try
            {
                await container.CreateItemAsync(
                    fresh,
                    new PartitionKey(endpointId),
                    new ItemRequestOptions { IfNoneMatchEtag = "*" })
                    .ConfigureAwait(false);
                return true;
            }
            catch (CosmosException createEx) when (createEx.StatusCode == HttpStatusCode.PreconditionFailed
                                                 || createEx.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }

        var existing = readResponse.Resource;
        // Treat null (field missing on a legacy doc) as the -1 sentinel.
        var current = existing.LastReplayedSequence ?? -1;
        if (current != expectedCurrent) return false;

        existing.LastReplayedSequence = newValue;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await container.ReplaceItemAsync(
                existing,
                existing.Id,
                new PartitionKey(endpointId),
                new ItemRequestOptions { IfMatchEtag = readResponse.ETag })
                .ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var doc = await Read(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        return doc?.ActiveParkCount ?? 0;
    }

    /// <summary>
    /// Internal helper used by <c>CosmosDbParkedMessageStore</c> to keep the
    /// active-park counter consistent with the parked-message rows. Bumps by
    /// <paramref name="delta"/> (positive on park, negative on replay/skip/
    /// dead-letter) and clamps at zero.
    /// </summary>
    internal async Task BumpActiveParkCount(string endpointId, string sessionId, int delta, CancellationToken cancellationToken = default)
    {
        if (delta == 0) return;
        var doc = await ReadOrCreate(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
        var next = doc.ActiveParkCount + delta;
        doc.ActiveParkCount = next < 0 ? 0 : next;
        await Upsert(doc, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SessionStateDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("endpointId")]
    public string EndpointId { get; set; } = default!;

    [JsonProperty("sessionId")]
    public string SessionId { get; set; } = default!;

    [JsonProperty("blockedByEventId", NullValueHandling = NullValueHandling.Ignore)]
    public string? BlockedByEventId { get; set; }

    [JsonProperty("deferredCount")]
    public int DeferredCount { get; set; }

    [JsonProperty("nextDeferralSequence")]
    public int NextDeferralSequence { get; set; }

    /// <summary>
    /// Per-session checkpoint of the highest ParkSequence already replayed by
    /// PortableDeferredMessageProcessor. <c>null</c> sentinel (treated as -1 by
    /// the store) = no replay has occurred. Stored as a nullable so legacy
    /// documents written before the field existed deserialize as null rather
    /// than as 0 (which would silently skip the first parked message at
    /// sequence 0).
    /// </summary>
    [JsonProperty("lastReplayedSequence", NullValueHandling = NullValueHandling.Ignore)]
    public int? LastReplayedSequence { get; set; }

    /// <summary>
    /// Hot-path counter of non-terminal parked messages for the session.
    /// Maintained by CosmosDbParkedMessageStore on park / replay / skip /
    /// dead-letter; reconciliation against IParkedMessageStore.CountActiveAsync
    /// happens at end-of-replay.
    /// </summary>
    [JsonProperty("activeParkCount")]
    public int ActiveParkCount { get; set; }

    [JsonProperty("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}
