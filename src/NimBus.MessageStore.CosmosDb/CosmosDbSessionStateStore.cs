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

    // TODO: task #5 implementation — issue #20
    public Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    // TODO: task #5 implementation — issue #20
    public Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // TODO: task #5 implementation — issue #20
    public Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
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

    [JsonProperty("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}
