using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB-backed implementation of <see cref="IParkedMessageStore"/>. Documents
/// live in a per-database <c>parked-messages</c> container, partitioned by
/// <c>/endpointId</c> with deterministic <c>id = "{endpointId}|{messageId}"</c>
/// (the natural idempotency key). Active-row queries filter on
/// <c>(endpointId, sessionKey, parkSequence &gt; afterSequence)</c> with all three
/// terminal-state timestamps unset.
///
/// Park-time idempotency is the <c>IfNoneMatchEtag = "*"</c> option on
/// <c>CreateItemAsync</c>: a re-park returns 409 Conflict, which the store
/// translates into "duplicate — return existing sequence". Sequence allocation
/// goes through <see cref="ISessionStateStore.GetNextDeferralSequenceAndIncrement"/>
/// so the counter stays consistent with the legacy session-state counter.
///
/// Active-park-count maintenance: park increments
/// <see cref="CosmosDbSessionStateStore"/>'s counter; replay/skip/dead-letter
/// each decrement it on the actual transition (idempotent on the parked row,
/// so the decrement only fires once per row).
///
/// See <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
/// §3.3 for the document shape and TTL-on-terminal-state design.
/// </summary>
public sealed class CosmosDbParkedMessageStore : IParkedMessageStore
{
    private readonly ICosmosClientAdapter _cosmosClient;
    private readonly CosmosDbSessionStateStore _sessionStateStore;
    private const string DatabaseId = "MessageDatabase";
    private const string ContainerId = "parked-messages";
    // 30-day TTL on terminal-state transitions (replayed / skipped / dead-lettered)
    // mirrors the existing MessageAudits retention window.
    private const int TerminalRowTtlSeconds = 30 * 24 * 60 * 60;

    public CosmosDbParkedMessageStore(CosmosClient cosmosClient, CosmosDbSessionStateStore sessionStateStore)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(sessionStateStore);
        _cosmosClient = new CosmosClientAdapter(cosmosClient);
        _sessionStateStore = sessionStateStore;
    }

    internal CosmosDbParkedMessageStore(ICosmosClientAdapter cosmosClient, CosmosDbSessionStateStore sessionStateStore)
    {
        _cosmosClient = cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient));
        _sessionStateStore = sessionStateStore ?? throw new ArgumentNullException(nameof(sessionStateStore));
    }

    private async Task<ICosmosContainerAdapter> GetContainer()
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await db.CreateContainerIfNotExistsAsync(ContainerId, "/endpointId").ConfigureAwait(false);
    }

    private static string DocumentId(string endpointId, string messageId) => $"{endpointId}|{messageId}";

    public async Task<long> ParkAsync(ParkedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var container = await GetContainer().ConfigureAwait(false);
        var docId = DocumentId(message.EndpointId, message.MessageId);

        // Allocate the sequence first. On 409 we waste the allocation, but
        // duplicate parks are operator-rare (idempotent receiver-replay paths).
        // The design accepts gaps in the sequence for this reason — the replay
        // loop reads ordered by ParkSequence ASC and skips terminal rows; gaps
        // are invisible.
        var sequence = await _sessionStateStore
            .GetNextDeferralSequenceAndIncrement(message.EndpointId, message.SessionKey, cancellationToken)
            .ConfigureAwait(false);

        var doc = new ParkedMessageDocument
        {
            Id = docId,
            EndpointId = message.EndpointId,
            SessionKey = message.SessionKey,
            ParkSequence = sequence,
            MessageId = message.MessageId,
            EventId = message.EventId,
            EventTypeId = message.EventTypeId,
            BlockingEventId = message.BlockingEventId,
            MessageEnvelopeJson = message.MessageEnvelopeJson,
            ParkedAtUtc = message.ParkedAtUtc == default ? DateTime.UtcNow : message.ParkedAtUtc,
            ReplayAttemptCount = message.ReplayAttemptCount,
        };

        try
        {
            await container.CreateItemAsync(
                doc,
                new PartitionKey(message.EndpointId),
                new ItemRequestOptions { IfNoneMatchEtag = "*" })
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Idempotent re-park: read the existing sequence and return it.
            var existing = await ReadDoc(message.EndpointId, message.MessageId, cancellationToken).ConfigureAwait(false);
            return existing?.ParkSequence ?? sequence;
        }

        // Fresh park — bump the active-park counter.
        await _sessionStateStore.BumpActiveParkCount(message.EndpointId, message.SessionKey, +1, cancellationToken).ConfigureAwait(false);

        message.ParkSequence = sequence;
        return sequence;
    }

    public async Task<IReadOnlyList<ParkedMessage>> GetActiveAsync(string endpointId, string sessionKey, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = limit > 0 ? limit : 100;

        var container = await GetContainer().ConfigureAwait(false);
        var query = new QueryDefinition(
            "SELECT TOP @PageSize * FROM c " +
            "WHERE c.endpointId = @EndpointId " +
            "AND c.sessionKey = @SessionKey " +
            "AND c.parkSequence > @AfterSequence " +
            "AND (NOT IS_DEFINED(c.replayedAtUtc) OR IS_NULL(c.replayedAtUtc)) " +
            "AND (NOT IS_DEFINED(c.skippedAtUtc) OR IS_NULL(c.skippedAtUtc)) " +
            "AND (NOT IS_DEFINED(c.deadLetteredAtUtc) OR IS_NULL(c.deadLetteredAtUtc)) " +
            "ORDER BY c.parkSequence ASC")
            .WithParameter("@PageSize", pageSize)
            .WithParameter("@EndpointId", endpointId)
            .WithParameter("@SessionKey", sessionKey)
            .WithParameter("@AfterSequence", afterSequence);

        var iterator = container.GetItemQueryIterator<ParkedMessageDocument>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(endpointId) });
        var rows = new List<ParkedMessage>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var doc in page) rows.Add(MapDoc(doc));
        }
        return rows;
    }

    public async Task MarkReplayedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await ReadDoc(endpointId, messageId, cancellationToken).ConfigureAwait(false);
        if (doc is null) return;
        // Idempotent: if the row is already in any terminal state, leave it.
        if (doc.ReplayedAtUtc.HasValue || doc.SkippedAtUtc.HasValue || doc.DeadLetteredAtUtc.HasValue) return;

        doc.ReplayedAtUtc = DateTime.UtcNow;
        // Set 30-day TTL on terminal-state transition so the row eventually
        // garbage-collects. Live (non-terminal) rows must NEVER carry a TTL —
        // it would lose ordering correctness.
        doc.TimeToLive = TerminalRowTtlSeconds;

        var container = await GetContainer().ConfigureAwait(false);
        await container.UpsertItemAsync(doc, new PartitionKey(endpointId)).ConfigureAwait(false);
        await _sessionStateStore.BumpActiveParkCount(endpointId, doc.SessionKey, -1, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSkippedAsync(string endpointId, string sessionKey, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return;

        var container = await GetContainer().ConfigureAwait(false);
        int transitioned = 0;
        foreach (var messageId in messageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = await ReadDoc(endpointId, messageId, cancellationToken).ConfigureAwait(false);
            if (doc is null) continue;
            if (doc.ReplayedAtUtc.HasValue || doc.SkippedAtUtc.HasValue || doc.DeadLetteredAtUtc.HasValue) continue;

            doc.SkippedAtUtc = DateTime.UtcNow;
            doc.TimeToLive = TerminalRowTtlSeconds;
            await container.UpsertItemAsync(doc, new PartitionKey(endpointId)).ConfigureAwait(false);
            transitioned++;
        }

        if (transitioned > 0)
        {
            await _sessionStateStore.BumpActiveParkCount(endpointId, sessionKey, -transitioned, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<int> IncrementReplayAttemptAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await ReadDoc(endpointId, messageId, cancellationToken).ConfigureAwait(false);
        if (doc is null) return 0;
        doc.ReplayAttemptCount += 1;
        var container = await GetContainer().ConfigureAwait(false);
        await container.UpsertItemAsync(doc, new PartitionKey(endpointId)).ConfigureAwait(false);
        return doc.ReplayAttemptCount;
    }

    public async Task MarkDeadLetteredAsync(string endpointId, string messageId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await ReadDoc(endpointId, messageId, cancellationToken).ConfigureAwait(false);
        if (doc is null) return;
        if (doc.ReplayedAtUtc.HasValue || doc.SkippedAtUtc.HasValue || doc.DeadLetteredAtUtc.HasValue) return;

        doc.DeadLetteredAtUtc = DateTime.UtcNow;
        doc.DeadLetterReason = reason;
        doc.TimeToLive = TerminalRowTtlSeconds;

        var container = await GetContainer().ConfigureAwait(false);
        await container.UpsertItemAsync(doc, new PartitionKey(endpointId)).ConfigureAwait(false);
        await _sessionStateStore.BumpActiveParkCount(endpointId, doc.SessionKey, -1, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAsync(string endpointId, string sessionKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = await GetContainer().ConfigureAwait(false);
        var query = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c " +
            "WHERE c.endpointId = @EndpointId " +
            "AND c.sessionKey = @SessionKey " +
            "AND (NOT IS_DEFINED(c.replayedAtUtc) OR IS_NULL(c.replayedAtUtc)) " +
            "AND (NOT IS_DEFINED(c.skippedAtUtc) OR IS_NULL(c.skippedAtUtc)) " +
            "AND (NOT IS_DEFINED(c.deadLetteredAtUtc) OR IS_NULL(c.deadLetteredAtUtc))")
            .WithParameter("@EndpointId", endpointId)
            .WithParameter("@SessionKey", sessionKey);

        var iterator = container.GetItemQueryIterator<int>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(endpointId) });
        if (!iterator.HasMoreResults) return 0;
        var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        return page.FirstOrDefault();
    }

    private async Task<ParkedMessageDocument?> ReadDoc(string endpointId, string messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = await GetContainer().ConfigureAwait(false);
        try
        {
            var response = await container.ReadItemAsync<ParkedMessageDocument>(
                DocumentId(endpointId, messageId),
                new PartitionKey(endpointId)).ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ParkedMessage MapDoc(ParkedMessageDocument doc) => new()
    {
        EndpointId = doc.EndpointId,
        SessionKey = doc.SessionKey,
        ParkSequence = doc.ParkSequence,
        MessageId = doc.MessageId,
        EventId = doc.EventId,
        EventTypeId = doc.EventTypeId ?? string.Empty,
        BlockingEventId = doc.BlockingEventId,
        MessageEnvelopeJson = doc.MessageEnvelopeJson,
        ParkedAtUtc = doc.ParkedAtUtc,
        ReplayedAtUtc = doc.ReplayedAtUtc,
        SkippedAtUtc = doc.SkippedAtUtc,
        DeadLetteredAtUtc = doc.DeadLetteredAtUtc,
        DeadLetterReason = doc.DeadLetterReason,
        ReplayAttemptCount = doc.ReplayAttemptCount,
    };
}

internal sealed class ParkedMessageDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("endpointId")]
    public string EndpointId { get; set; } = default!;

    [JsonProperty("sessionKey")]
    public string SessionKey { get; set; } = default!;

    [JsonProperty("parkSequence")]
    public long ParkSequence { get; set; }

    [JsonProperty("messageId")]
    public string MessageId { get; set; } = default!;

    [JsonProperty("eventId")]
    public string EventId { get; set; } = default!;

    [JsonProperty("eventTypeId", NullValueHandling = NullValueHandling.Ignore)]
    public string? EventTypeId { get; set; }

    [JsonProperty("blockingEventId", NullValueHandling = NullValueHandling.Ignore)]
    public string? BlockingEventId { get; set; }

    [JsonProperty("messageEnvelopeJson")]
    public string MessageEnvelopeJson { get; set; } = default!;

    [JsonProperty("parkedAtUtc")]
    public DateTime ParkedAtUtc { get; set; }

    [JsonProperty("replayedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? ReplayedAtUtc { get; set; }

    [JsonProperty("skippedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? SkippedAtUtc { get; set; }

    [JsonProperty("deadLetteredAtUtc", NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? DeadLetteredAtUtc { get; set; }

    [JsonProperty("deadLetterReason", NullValueHandling = NullValueHandling.Ignore)]
    public string? DeadLetterReason { get; set; }

    [JsonProperty("replayAttemptCount")]
    public int ReplayAttemptCount { get; set; }

    /// <summary>
    /// Cosmos TTL — only set on terminal-state transition (replayed / skipped /
    /// dead-lettered). Live rows must NEVER carry a TTL or they'd be GC'd while
    /// still active.
    /// </summary>
    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLive { get; set; }
}
