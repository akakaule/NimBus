using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

public interface ICosmosDbClient
{
    Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content);

    Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content);

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content);

    Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount);
    Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default);
    Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds);
    Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId);
    Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId);
    Task<UnresolvedEvent> GetEvent(string endpointId, string eventId);
    Task<UnresolvedEvent> GetEventById(string endpointId, string eventId);
    Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds);

    Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId);

    Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId);
    Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds);
    Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId);
    Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken);

    Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take);
    Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId);
    Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId);

    Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail, string type,
        string author, string url, List<string> eventTypes, string payload, int frequency);

    Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId);

    Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint,
        string eventtypes, string payload, string errorText);

    Task<string> GetEndpointErrorList(string endpointId);
    Task<bool> UpdateSubscription(EndpointSubscription subscription);
    Task<bool> UnsubscribeById(string endpointId, string mail);
    Task<bool> DeleteSubscription(string subscriptionId);
    Task<bool> UnsubscribeByMail(string endpointId, string mail);

    Task<bool> PurgeMessages(string endpointId, string sessionId);
    Task<bool> PurgeMessages(string endpointId);

    Task<EndpointMetadata> GetEndpointMetadata(string endpointId);
    Task<List<EndpointMetadata>> GetMetadatas();
    Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds);
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);

    // Message search (cross-partition query on messages container)
    Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount);

    // Message history (replaces IMessageStoreClient blob operations)
    Task StoreMessage(MessageEntity message);
    Task<MessageEntity> GetMessage(string eventId, string messageId);
    Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId);
    Task<MessageEntity> GetLatestEventRequestMessage(string eventId);
    Task<MessageEntity> GetFailedMessage(string eventId, string endpointId);
    Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId);

    Task RemoveStoredMessage(string eventId, string messageId);

    // Audit trail
    Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null);
    Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId);
    Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount);

    // Failed event archive
    Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId);

    // Metrics aggregation
    Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from);

    Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from);
    Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from);
    Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel);
}

public class CosmosDbClient : ICosmosDbClient, NimBus.MessageStore.Abstractions.INimBusMessageStore
{
    private readonly ICosmosClientAdapter _cosmosClient;
    private readonly ILogger _logger;

    // Cosmos container handles are lightweight client-side proxies the SDK
    // recommends caching for the client's lifetime. Every data operation used to
    // call CreateContainerIfNotExistsAsync, which issues a control-plane
    // round-trip on each call even when the container already exists. Cache the
    // resolved handle (running the one-time "ensure exists" once per container)
    // so steady-state reads/writes skip that round-trip. Keyed by container id,
    // which is unique per physical container in the database. Entries are evicted
    // when a container is deleted (PurgeMessages) so the next access recreates it.
    private readonly ConcurrentDictionary<string, Task<ICosmosContainerAdapter>> _containerCache = new();

    private const string DatabaseId = "MessageDatabase";

    // Hot-path writes only ever inspect StatusCode on the response; skipping the
    // response body (which echoes the whole document, EventJson included) saves
    // egress bytes and response-deserialization on every tracked message.
    private static readonly ItemRequestOptions SuppressContentOnWrite = new() { EnableContentResponseOnWrite = false };

    private const string PendingStatus = "Pending";
    private const string FailedStatus = "Failed";
    private const string DeferredStatus = "Deferred";
    private const string DLQStatus = "DeadLettered";
    private const string UnsupportedStatus = "Unsupported";
    private const string CompletedStatus = "Completed";
    private const string SkippedStatus = "Skipped";

    private const string PublisherRole = "Publisher";
    private const string SubscriptionsContainer = "subscriptions";
    private const string MessagesContainer = "messages";
    private const string AuditsContainer = "audits";
    private const string EventSchemasContainer = "eventschemas";

    public CosmosDbClient(CosmosClient cosmosClient, ILogger<CosmosDbClient> logger = null)
    {
        _logger = logger;
        _cosmosClient = new CosmosClientAdapter(cosmosClient, _logger);
    }

    public CosmosDbClient(ICosmosClientAdapter cosmosClient, ILogger<CosmosDbClient> logger = null)
    {
        _logger = logger;
        _cosmosClient = new TransientTranslatingCosmosClientAdapter(cosmosClient, _logger);
    }

    [Obsolete("Use the Microsoft.Extensions.Logging constructor — NimBus standardizes on Microsoft.Extensions.Logging (ADR-006). This bridge remains for callers that still pass a Serilog logger.")]
    public CosmosDbClient(CosmosClient cosmosClient, Serilog.ILogger logger)
    {
        _logger = logger is null ? null : new SerilogBridgeLogger(logger);
        _cosmosClient = new CosmosClientAdapter(cosmosClient, _logger);
    }

    [Obsolete("Use the Microsoft.Extensions.Logging constructor — NimBus standardizes on Microsoft.Extensions.Logging (ADR-006). This bridge remains for callers that still pass a Serilog logger.")]
    public CosmosDbClient(ICosmosClientAdapter cosmosClient, Serilog.ILogger logger)
    {
        _logger = logger is null ? null : new SerilogBridgeLogger(logger);
        _cosmosClient = new TransientTranslatingCosmosClientAdapter(cosmosClient, _logger);
    }

    /// <summary>
    /// Forwards Microsoft.Extensions.Logging calls to a caller-supplied Serilog logger.
    /// Only used by the obsolete bridge constructors.
    /// </summary>
    private sealed class SerilogBridgeLogger : ILogger
    {
        private readonly Serilog.ILogger _serilog;

        public SerilogBridgeLogger(Serilog.ILogger serilog) => _serilog = serilog;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _serilog.IsEnabled(ToSerilogLevel(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _serilog.Write(ToSerilogLevel(logLevel), exception, "{Message}", formatter(state, exception));

        private static Serilog.Events.LogEventLevel ToSerilogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
            LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            LogLevel.Information => Serilog.Events.LogEventLevel.Information,
            LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            LogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Fatal,
        };
    }


    public async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        const string sqlQuery =
            "SELECT COUNT(1) AS EventCount, c.status FROM c WHERE (NOT IS_DEFINED(c.deleted) or c.deleted != true) GROUP BY c.status";
        var queryDefinition = new QueryDefinition(sqlQuery);

        var result = container.GetItemQueryIterator<StatusQueryResult>(queryDefinition);
        var resultDict = new Dictionary<string, int>();
        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                resultDict.Add(queryResult.Status, queryResult.EventCount);
            }
        }

        return new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            DeferredCount = resultDict.ContainsKey(DeferredStatus) ? resultDict[DeferredStatus] : 0,
            PendingCount = resultDict.ContainsKey(PendingStatus) ? resultDict[PendingStatus] : 0,
            FailedCount = resultDict.ContainsKey(FailedStatus) ? resultDict[FailedStatus] : 0,
            DeadletterCount = resultDict.ContainsKey(DLQStatus) ? resultDict[DLQStatus] : 0,
            UnsupportedCount = resultDict.ContainsKey(UnsupportedStatus) ? resultDict[UnsupportedStatus] : 0,
        };
    }

    public async Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
    {
        var container = await GetEndpointContainer(endpointId);
        var queryDefinition = new QueryDefinition(
            "SELECT c.id, c.status FROM c WHERE c.status IN (@pendingStatus, @deferredStatus) AND c.sessionId = @sessionId AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@sessionId", sessionId);

        var result = container.GetItemQueryIterator<SessionCountQueryResult>(queryDefinition);

        var sessionResults = new List<SessionCountQueryResult>();

        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                sessionResults.Add(queryResult);
            }
        }

        return new SessionStateCount
        {
            SessionId = sessionId,
            DeferredEvents = sessionResults
                .Where(se => se.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase))
                .Select(se => se.Id),
            PendingEvents = sessionResults
                .Where(se => se.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase))
                .Select(se => se.Id)
        };
    }

    public async Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
    {
        var sessionIdList = sessionIds.ToList();
        if (sessionIdList.Count == 0)
            return Enumerable.Empty<SessionStateCount>();

        var container = await GetEndpointContainer(endpointId);
        var queryDefinition = new QueryDefinition(
            "SELECT c.id, c.status, c.sessionId FROM c WHERE c.status IN (@pendingStatus, @deferredStatus) AND ARRAY_CONTAINS(@sessionIds, c.sessionId) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@sessionIds", sessionIdList);

        var result = container.GetItemQueryIterator<BatchSessionQueryResult>(queryDefinition);
        var allResults = new List<BatchSessionQueryResult>();

        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                allResults.Add(queryResult);
            }
        }

        return allResults
            .GroupBy(r => r.SessionId)
            .Select(g => new SessionStateCount
            {
                SessionId = g.Key,
                DeferredEvents = g.Where(r => r.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id),
                PendingEvents = g.Where(r => r.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id)
            });
    }

    public async Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize,
        string continuationToken)
    {
        var container = await GetEndpointContainer(endpointId);

        var requestOptions = new QueryRequestOptions
        {
            MaxItemCount = pageSize
        };

        try
        {
            FeedIterator<EventDbo> result = container.GetItemLinqQueryable<EventDbo>(
                    true,
                    String.IsNullOrEmpty(continuationToken) ? null : continuationToken,
                    requestOptions)
                .Where(e => e.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(FailedStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(DLQStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(UnsupportedStatus, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Deleted.HasValue || !e.Deleted.Value)
                .OrderByDescending(e => e.Event.UpdatedAt)
                // Server-side projection: every UnresolvedEvent property EXCEPT the
                // heavy EventJson payload, which dominates the response size and is
                // never surfaced by the endpoint page list (the detail view fetches
                // it on demand). Same shape as GetEventsByFilter — keep in sync with
                // that projection and the UnresolvedEvent drift guard.
                .Select(x => new EventDbo
                {
                    Id = x.Id,
                    Status = x.Status,
                    EventType = x.EventType,
                    SessionId = x.SessionId,
                    Deleted = x.Deleted,
                    Event = new UnresolvedEvent
                    {
                        UpdatedAt = x.Event.UpdatedAt,
                        EnqueuedTimeUtc = x.Event.EnqueuedTimeUtc,
                        EventId = x.Event.EventId,
                        SessionId = x.Event.SessionId,
                        CorrelationId = x.Event.CorrelationId,
                        ResolutionStatus = x.Event.ResolutionStatus,
                        EndpointRole = x.Event.EndpointRole,
                        EndpointId = x.Event.EndpointId,
                        RetryCount = x.Event.RetryCount,
                        RetryLimit = x.Event.RetryLimit,
                        MessageType = x.Event.MessageType,
                        DeadLetterReason = x.Event.DeadLetterReason,
                        DeadLetterErrorDescription = x.Event.DeadLetterErrorDescription,
                        LastMessageId = x.Event.LastMessageId,
                        OriginatingMessageId = x.Event.OriginatingMessageId,
                        ParentMessageId = x.Event.ParentMessageId,
                        Reason = x.Event.Reason,
                        OriginatingFrom = x.Event.OriginatingFrom,
                        EventTypeId = x.Event.EventTypeId,
                        To = x.Event.To,
                        From = x.Event.From,
                        MessageContent = new MessageContent
                        {
                            // EventJson deliberately omitted — the sole purpose of
                            // this projection.
                            EventContent = new EventContent
                            {
                                EventTypeId = x.Event.MessageContent.EventContent.EventTypeId,
                            },
                            ErrorContent = x.Event.MessageContent.ErrorContent,
                        },
                        QueueTimeMs = x.Event.QueueTimeMs,
                        ProcessingTimeMs = x.Event.ProcessingTimeMs,
                        PendingSubStatus = x.Event.PendingSubStatus,
                        HandoffReason = x.Event.HandoffReason,
                        ExternalJobId = x.Event.ExternalJobId,
                        ExpectedBy = x.Event.ExpectedBy,
                        CloudEventId = x.Event.CloudEventId,
                        CloudEventSource = x.Event.CloudEventSource,
                        CloudEventType = x.Event.CloudEventType,
                        CloudEventSubject = x.Event.CloudEventSubject,
                    },
                })
                .ToFeedIterator();
            result = CosmosExceptionTranslation.Wrap(result, _logger);

            var pendingEvents = new List<string>();
            var failedEvents = new List<string>();
            var deferredEvents = new List<string>();
            var deadletteredEvents = new List<string>();
            var unsupportedEvents = new List<string>();
            var unresolvedEvents = new List<UnresolvedEvent>();
            var token = "";
            if (result.HasMoreResults)
            {
                var feed = await result.ReadNextAsync();
                token = feed.ContinuationToken;
                foreach (var eventDbo in feed)
                {
                    unresolvedEvents.Add(eventDbo.Event);
                    var status = eventDbo.Status;
                    switch (status)
                    {
                        case FailedStatus:
                            failedEvents.Add(eventDbo.Id);
                            break;
                        case PendingStatus:
                            pendingEvents.Add(eventDbo.Id);
                            break;
                        case DeferredStatus:
                            deferredEvents.Add(eventDbo.Id);
                            break;
                        case DLQStatus:
                            deadletteredEvents.Add(eventDbo.Id);
                            break;
                        case UnsupportedStatus:
                            unsupportedEvents.Add(eventDbo.Id);
                            break;
                        default:
                            break;
                    }
                }
            }

            var endpointState = new EndpointState
            {
                EndpointId = endpointId,
                DeferredEvents = deferredEvents,
                PendingEvents = pendingEvents,
                FailedEvents = failedEvents,
                DeadletteredEvents = deadletteredEvents,
                UnsupportedEvents = unsupportedEvents,
                EnrichedUnresolvedEvents = unresolvedEvents,
                EventTime = DateTime.UtcNow,
                ContinuationToken = token
            };

            return endpointState;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("COSMOS PAGING: Endpoint container not found for '{EndpointId}'", endpointId);
            return null;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS PAGING-ERROR: Failed to download endpoint state for '{EndpointId}'", endpointId);
            throw;
        }
    }

    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, DeferredStatus);

    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, FailedStatus);

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, PendingStatus);

    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent contet) =>
        UploadMessage(eventId, sessionId, endpointId, contet, DLQStatus);

    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, UnsupportedStatus);

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadCompletedMessage(eventId, sessionId, endpointId, content, SkippedStatus);

    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadCompletedMessage(eventId, sessionId, endpointId, content, CompletedStatus);

    private async Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content, string status)
    {
        var container = await GetEndpointContainer(endpointId);
        //var cosmosEvent = await GetPendingEvent(endpointId, eventId, sessionId);
        //cosmosEvent.ResolutionStatus = resolutionStatus;
        var eventDbo = new EventDbo
        {
            Id = $"{eventId}_{sessionId}",
            Event = content,
            SessionId = sessionId,
            Status = status,
            EventType = content.EventTypeId,
            Deleted = true,
            TimeToLive = 60 * 60 * 24 * 30 // 30 days TTL
        };

        try
        {
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id), SuppressContentOnWrite);
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}, Status: {Status}", eventId, sessionId, response.StatusCode, CompletedStatus);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: EventId: {EventId}, SessionId: {SessionId}, Status: {Status}", eventId, sessionId, CompletedStatus);
            throw;
        }
    }

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";

        try
        {
            // The doc id (eventId_sessionId) fully identifies the document and the
            // container is partitioned by /id, so soft-delete it with a single patch
            // (mirrors ArchiveFailedEvent) instead of a query + full-document upsert
            // that would drag the heavy EventJson payload across the wire 3× over 2
            // round-trips.
            var response = await container.PatchItemAsync<EventDbo>(id, new PartitionKey(id), new[]
            {
                PatchOperation.Set("/deleted", true),
                PatchOperation.Set("/ttl", 60) // 1 Minute
            });
            _logger?.LogTrace(
                "COSMOS REMOVE-MESSAGE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}", eventId, sessionId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-MESSAGE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}", eventId, sessionId, e.StatusCode);

            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Missing document — nothing to remove (matches the previous
                // empty-query-result path returning false).
                return false;
            }

            throw;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        try
        {
            var container = await GetEndpointContainer(endpointId);
            // Only the id is needed for the deletes — don't pull whole EventDbo
            // documents (the EventJson payload dominates the response size).
            var queryDefinition = new QueryDefinition("SELECT c.id FROM c WHERE c.sessionId = @sessionId")
                .WithParameter("@sessionId", sessionId);
            var result = container.GetItemQueryIterator<IdProjection>(queryDefinition);

            _logger?.LogInformation(
                "COSMOS PURGE: Deleted all messages on endpoint {EndpointId} in session {SessionId}", endpointId, sessionId);
            // The container is partitioned by /id, so each document lives in its own
            // logical partition and a TransactionalBatch (single-partition only) can't
            // apply. The deletes are independent — run them concurrently (bounded)
            // instead of one round-trip at a time.
            var deleteOptions = new ParallelOptions { MaxDegreeOfParallelism = 8 };
            while (result.HasMoreResults)
            {
                var page = await result.ReadNextAsync();
                await Parallel.ForEachAsync(page, deleteOptions, async (item, _) =>
                {
                    await container.DeleteItemAsync<EventDbo>(item.Id, new PartitionKey(item.Id));
                });
            }

            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS PURGE: Couldn't delete all messages on endpoint {EndpointId} in session {SessionId}", endpointId, sessionId);
            return false;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        try
        {
            var container = await GetEndpointContainer(endpointId);

            await container.DeleteContainerAsync();
            // Drop the cached handle so the next access re-runs "ensure exists"
            // and recreates the now-deleted container instead of reusing a stale
            // handle that would only throw NotFound.
            _containerCache.TryRemove(endpointId, out _);
            _logger?.LogInformation("COSMOS PURGE: Deleted all messages on endpoint {EndpointId}", endpointId);

            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS PURGE: Couldn't delete all messages on endpoint {EndpointId}", endpointId);
            return false;
        }
    }

    /// <summary>
    /// <c>c.status</c> (the write-side authority) is not mirrored into the
    /// embedded event document on upsert, so reads must hydrate
    /// <see cref="UnresolvedEvent.ResolutionStatus"/> from it — otherwise every
    /// Cosmos read reports the enum default. The SQL and in-memory providers
    /// map status on read the same way.
    /// </summary>
    private static UnresolvedEvent HydrateResolutionStatus(EventDbo dbo)
    {
        if (dbo?.Event != null && Enum.TryParse<ResolutionStatus>(dbo.Status, out var status))
        {
            dbo.Event.ResolutionStatus = status;
        }

        return dbo?.Event;
    }

    public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, PendingStatus);

    public async Task<UnresolvedEvent> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalJobId)) return null;
        var container = await GetEndpointContainer(endpointId);
        // Filter on event.ExternalJobId (the persisted location) plus the
        // pending-handoff discriminator. Container is partitioned per endpoint
        // already so this query stays within one partition.
        var queryDefinition = new QueryDefinition(
                @"SELECT TOP 1 * FROM c
                  WHERE c.event.ExternalJobId = @x
                    AND c.event.PendingSubStatus = 'Handoff'
                    AND c.status = @status
                    AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true)")
            .WithParameter("@x", externalJobId)
            .WithParameter("@status", PendingStatus);
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync(cancellationToken);
            if (eventDbo.Any())
            {
                return HydrateResolutionStatus(eventDbo.First());
            }
        }

        return null;
    }

    public async Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds)
    {
        var container = await GetEndpointContainer(endpointId);
        // Bound to a single row and filter status/sub-status/event-type server-side so the agent
        // receive long-poll no longer materialises every pending event. Container is partitioned
        // per endpoint, so this stays within one partition. Event types are passed via
        // ARRAY_CONTAINS over a parameter to keep the SQL parameterised.
        var sql = @"SELECT TOP 1 * FROM c
                    WHERE c.event.PendingSubStatus = 'Handoff'
                      AND c.status = @status
                      AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true)";
        var types = eventTypeIds?.Where(t => !string.IsNullOrEmpty(t)).ToArray();
        if (types is { Length: > 0 })
            sql += " AND ARRAY_CONTAINS(@eventTypeIds, c.event.EventTypeId)";
        sql += " ORDER BY c.event.EnqueuedTimeUtc ASC";

        var queryDefinition = new QueryDefinition(sql).WithParameter("@status", PendingStatus);
        if (types is { Length: > 0 })
            queryDefinition = queryDefinition.WithParameter("@eventTypeIds", types);

        var result = container.GetItemQueryIterator<EventDbo>(
            queryDefinition,
            requestOptions: new QueryRequestOptions { MaxItemCount = 1 });

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
                return eventDbo.First().Event;
        }

        return null;
    }

    public Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, FailedStatus);

    public Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, DeferredStatus);

    public Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, DLQStatus);

    public Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, UnsupportedStatus);


    public async Task<UnresolvedEvent> GetEvent(string endpointId, string eventId)
    {
        var container = await GetEndpointContainer(endpointId);
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.event.EventId = @eventId")
            .WithParameter("@eventId", eventId);
        // Lookup-by-eventId on a container partitioned by /id (eventId_sessionId), so it
        // necessarily fans across partitions; event.EventId is already covered by the
        // default range index (a composite index can't improve a single equality with no
        // ORDER BY). Cap the fetch to the single document the caller actually reads so RU
        // and payload don't scale with how many session-events share the eventId.
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition, null,
            new QueryRequestOptions { MaxItemCount = 1 });

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
            {
                return HydrateResolutionStatus(eventDbo.First());
            }
        }

        return null;
    }


    private async Task<UnresolvedEvent> GetEvent(string endpointId, string eventId, string sessionId, string status)
    {
        var container = await GetEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            // The doc id (eventId_sessionId) fully identifies the document and the
            // container is partitioned by /id, so a point read (mirroring
            // GetEventById) costs ~1/3 the RU of the equivalent query. The
            // status + not-deleted filters move in-memory with identical semantics.
            var rel = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id));
            var dbo = rel.Resource;
            if (dbo.Status != status || (dbo.Deleted ?? false))
            {
                return null;
            }

            return HydrateResolutionStatus(dbo);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<UnresolvedEvent> GetEventById(string endpointId, string id)
    {
        var container = await GetEndpointContainer(endpointId);
        try
        {
            var rel = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id), new ItemRequestOptions() { });
            return HydrateResolutionStatus(rel.Resource);
        }
        catch (CosmosException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            throw;
        }
    }

    public async Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var idList = eventIds.ToList();
        if (idList.Count == 0)
            return new List<UnresolvedEvent>();

        var container = await GetEndpointContainer(endpointId);
        const int batchSize = 50;
        var results = new List<UnresolvedEvent>();

        try
        {
            foreach (var batch in idList.Chunk(batchSize))
            {
                var items = batch.Select(id => (id, new PartitionKey(id))).ToList();
                var response = await container.ReadManyItemsAsync<EventDbo>(items);
                if (response.Resource != null)
                    results.AddRange(response.Resource.Select(HydrateResolutionStatus));
            }
            return results;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return results;
        }
    }

    public async Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken,
        int maxSearchItemsCount)
    {
        var container = await GetEndpointContainer(filter.EndPointId);
        var requestOptions = new QueryRequestOptions
            { MaxItemCount = PaginationLimits.Resolve(maxSearchItemsCount) };
        var queryable = container
            .GetItemLinqQueryable<EventDbo>(true,
                String.IsNullOrEmpty(continuationToken) ? null : continuationToken,
                requestOptions);
        var query = queryable.Where(x => true);

        // Datetimes
        if (filter.UpdatedAtFrom != null)
            query = query
                .Where(x => x.Event.UpdatedAt >= filter.UpdatedAtFrom);

        if (filter.UpdatedAtTo != null)
            query = query
                .Where(x => x.Event.UpdatedAt <= filter.UpdatedAtTo);

        if (filter.EnqueuedAtFrom != null)
            query = query
                .Where(x => x.Event.EnqueuedTimeUtc >= filter.EnqueuedAtFrom);

        if (filter.EnqueuedAtTo != null)
            query = query
                .Where(x => x.Event.EnqueuedTimeUtc <= filter.EnqueuedAtTo);

        // Strings
        if (filter.EventTypeId != null && filter.EventTypeId.Any())
            query = query
                .Where(x => filter.EventTypeId.Contains(x.EventType));


        // ID-like fields use case-insensitive PREFIX matching. The Cosmos LINQ
        // provider translates StartsWith with OrdinalIgnoreCase (and only that
        // comparison) to index-served STARTSWITH(x, y, true); Contains would be
        // a full-scan CONTAINS. Free-text To/From/Payload keep Contains.
        if (filter.EndPointId != null)
            query = query
                .Where(x => x.Event.EndpointId.StartsWith(filter.EndPointId, StringComparison.OrdinalIgnoreCase));

        if (filter.EventId != null)
            query = query
                .Where(x => x.Id.StartsWith(filter.EventId, StringComparison.OrdinalIgnoreCase));

        if (filter.SessionId != null)
            query = query
                .Where(x => x.SessionId.StartsWith(filter.SessionId, StringComparison.OrdinalIgnoreCase));

        if (filter.To != null)
            query = query
                .Where(x => x.Event.To.Contains(filter.To));

        if (filter.From != null)
            query = query
                .Where(x => x.Event.From.Contains(filter.From));

        if (filter.ResolutionStatus != null && filter.ResolutionStatus.Any())
            query = query
                .Where(x => filter.ResolutionStatus.Contains(x.Status));

        // MessageType is persisted as a string (StringEnumConverter) and no enum
        // name is a substring of another, so Contains(ToString()) is equality here —
        // use plain equality (as SearchMessages does) so the range index can serve
        // it instead of a full-scan CONTAINS over a computed ToString().
        if (filter.MessageType != null)
            query = query
                .Where(x => x.Event.MessageType == filter.MessageType);

        if (filter.Payload != null)
            query = query
                .Where(x => x.Event.MessageContent.EventContent.EventJson.Contains(filter.Payload));

        // Server-side projection: every UnresolvedEvent property EXCEPT the heavy
        // EventJson payload (search results never surface it; the detail view
        // fetches it on demand via GetLatestEventRequestMessage). ErrorContent is
        // projected whole (the error-grouped search view reads ErrorText).
        // Drift guard: MessageTrackingStoreConformanceTests reflects over
        // UnresolvedEvent's properties and fails when a new property is missing
        // from search results — extend this member-init when adding properties.
        var result = query
            .OrderByDescending(e => e.Event.UpdatedAt)
            .Select(x => new EventDbo
            {
                Id = x.Id,
                Status = x.Status,
                EventType = x.EventType,
                SessionId = x.SessionId,
                Deleted = x.Deleted,
                Event = new UnresolvedEvent
                {
                    UpdatedAt = x.Event.UpdatedAt,
                    EnqueuedTimeUtc = x.Event.EnqueuedTimeUtc,
                    EventId = x.Event.EventId,
                    SessionId = x.Event.SessionId,
                    CorrelationId = x.Event.CorrelationId,
                    ResolutionStatus = x.Event.ResolutionStatus,
                    EndpointRole = x.Event.EndpointRole,
                    EndpointId = x.Event.EndpointId,
                    RetryCount = x.Event.RetryCount,
                    RetryLimit = x.Event.RetryLimit,
                    MessageType = x.Event.MessageType,
                    DeadLetterReason = x.Event.DeadLetterReason,
                    DeadLetterErrorDescription = x.Event.DeadLetterErrorDescription,
                    LastMessageId = x.Event.LastMessageId,
                    OriginatingMessageId = x.Event.OriginatingMessageId,
                    ParentMessageId = x.Event.ParentMessageId,
                    Reason = x.Event.Reason,
                    OriginatingFrom = x.Event.OriginatingFrom,
                    EventTypeId = x.Event.EventTypeId,
                    To = x.Event.To,
                    From = x.Event.From,
                    MessageContent = new MessageContent
                    {
                        // EventJson deliberately omitted — the sole purpose of
                        // this projection.
                        EventContent = new EventContent
                        {
                            EventTypeId = x.Event.MessageContent.EventContent.EventTypeId,
                        },
                        ErrorContent = x.Event.MessageContent.ErrorContent,
                    },
                    QueueTimeMs = x.Event.QueueTimeMs,
                    ProcessingTimeMs = x.Event.ProcessingTimeMs,
                    PendingSubStatus = x.Event.PendingSubStatus,
                    HandoffReason = x.Event.HandoffReason,
                    ExternalJobId = x.Event.ExternalJobId,
                    ExpectedBy = x.Event.ExpectedBy,
                    CloudEventId = x.Event.CloudEventId,
                    CloudEventSource = x.Event.CloudEventSource,
                    CloudEventType = x.Event.CloudEventType,
                    CloudEventSubject = x.Event.CloudEventSubject,
                },
            })
            .ToFeedIterator();
        result = CosmosExceptionTranslation.Wrap(result, _logger);
        var events = new List<UnresolvedEvent>();
        var token = "";
        var effectiveLimit = PaginationLimits.Resolve(maxSearchItemsCount);
        while (result.HasMoreResults && events.Count <= effectiveLimit)
        {
            var eventDbo = await result.ReadNextAsync();
            token = eventDbo.ContinuationToken;
            foreach (var queryResult in eventDbo)
            {
                var ev = HydrateResolutionStatus(queryResult);
                // Search results never surface the full request payload — the detail
                // view fetches it on demand via GetLatestEventRequestMessage — so drop
                // the heavy EventJson blob, which otherwise dominates the response on a
                // 100-row page. ErrorContent and all metadata are kept (the error-grouped
                // search view reads ErrorText).
                if (ev?.MessageContent?.EventContent != null)
                {
                    ev.MessageContent.EventContent.EventJson = null;
                }
                events.Add(ev);
            }

            if (eventDbo.Count > 0)
            {
                return new SearchResponse { Events = events, ContinuationToken = token };
            }
        }

        return new SearchResponse { Events = events, ContinuationToken = token };
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        // Parameterized rather than interpolated so the SDK can cache the query plan.
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", CompletedStatus);
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition, null, new QueryRequestOptions { });
        var unresolvedEvents = new List<UnresolvedEvent>();

        while (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            foreach (var queryResult in eventDbo)
            {
                unresolvedEvents.Add(HydrateResolutionStatus(queryResult));
            }
        }

        return unresolvedEvents;
    }

    public async Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId,
        string sessionId, int skip, int take)
    {
        var safeSkip = skip < 0 ? 0 : skip;
        var safeTake = take <= 0 ? int.MaxValue : take;

        var container = await GetEndpointContainer(endpointId);

        // Only four fields feed BlockedMessageEvent — project them server-side
        // instead of reading whole EventDbo documents (large EventJson payloads).
        // The page query and the total count are independent — drain them
        // concurrently so the blocked-events dialog costs one round-trip's latency.
        async Task<List<BlockedMessageEvent>> DrainPageAsync()
        {
            var pageQuery = new QueryDefinition(
                "SELECT c.status, c.event.EventId AS eventId, c.event.OriginatingMessageId AS originatingMessageId, c.event.LastMessageId AS lastMessageId FROM c WHERE c.sessionId = @sessionId AND c.status IN (@pendingStatus, @deferredStatus) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true) ORDER BY c.event.UpdatedAt DESC OFFSET @skip LIMIT @take")
                .WithParameter("@sessionId", sessionId)
                .WithParameter("@pendingStatus", PendingStatus)
                .WithParameter("@deferredStatus", DeferredStatus)
                .WithParameter("@skip", safeSkip)
                .WithParameter("@take", safeTake);
            var pageIterator = container.GetItemQueryIterator<BlockedEventProjection>(pageQuery);
            var items = new List<BlockedMessageEvent>();
            while (pageIterator.HasMoreResults)
            {
                var page = await pageIterator.ReadNextAsync();
                foreach (var queryResult in page)
                {
                    items.Add(ToBlockedMessageEvent(queryResult));
                }
            }

            return items;
        }

        async Task<int> DrainCountAsync()
        {
            var countQuery = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.sessionId = @sessionId AND c.status IN (@pendingStatus, @deferredStatus) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
                .WithParameter("@sessionId", sessionId)
                .WithParameter("@pendingStatus", PendingStatus)
                .WithParameter("@deferredStatus", DeferredStatus);
            var countIterator = container.GetItemQueryIterator<int>(countQuery);
            var total = 0;
            while (countIterator.HasMoreResults)
            {
                var countResponse = await countIterator.ReadNextAsync();
                foreach (var c in countResponse) total += c;
            }

            return total;
        }

        var itemsTask = DrainPageAsync();
        var totalTask = DrainCountAsync();
        await Task.WhenAll(itemsTask, totalTask);

        return new BlockedMessageEventPage
        {
            Items = await itemsTask,
            Total = await totalTask,
        };
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var blockedMessageEvents = new List<UnresolvedEvent>();
        try
        {
            // Bound page size so a session with many pending events streams in
            // pages rather than one oversized response. (Full caller-driven
            // pagination would need an INimBusMessageStore signature change across
            // all providers — tracked separately.)
            FeedIterator<EventDbo> queryResult = container
                .GetItemLinqQueryable<EventDbo>(true, null, new QueryRequestOptions { MaxItemCount = 200 })
                .Where(e => e.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Deleted.HasValue || !e.Deleted.Value)
                .OrderByDescending(e => e.Event.UpdatedAt).ToFeedIterator();
            queryResult = CosmosExceptionTranslation.Wrap(queryResult, _logger);
            while (queryResult.HasMoreResults)
            {
                var eventDbo = await queryResult.ReadNextAsync();
                foreach (var pendingEvent in eventDbo)
                {
                    blockedMessageEvents.Add(HydrateResolutionStatus(pendingEvent));
                }
            }
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("COSMOS PENDING-EVENTS: Endpoint container not found for '{EndpointId}'", endpointId);
            return null;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS PENDING-EVENTS-ERROR: Failed to get pending events for endpoint '{EndpointId}'", endpointId);
            throw;
        }

        return blockedMessageEvents;
    }

    public async Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        // Parameterized for query-plan caching; projected because only four fields
        // feed BlockedMessageEvent (full documents carry the EventJson payload).
        var queryDefinition = new QueryDefinition(
            "SELECT c.status, c.event.EventId AS eventId, c.event.OriginatingMessageId AS originatingMessageId, c.event.LastMessageId AS lastMessageId FROM c WHERE c.event.EndpointRole = @publisherRole AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@publisherRole", PublisherRole);
        var result = container.GetItemQueryIterator<BlockedEventProjection>(queryDefinition);
        var invalidMessageEvents = new List<BlockedMessageEvent>();

        while (result.HasMoreResults)
        {
            var page = await result.ReadNextAsync();
            foreach (var queryResult in page)
            {
                invalidMessageEvents.Add(ToBlockedMessageEvent(queryResult));
            }
        }

        return invalidMessageEvents;
    }

    public async Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail,
        string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var formattedType = string.Equals(type, "mail", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "teams", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "mail;teams", StringComparison.OrdinalIgnoreCase)
            ? type.ToLower()
            : throw new Exception($"Invalid type.{type} valid: mail or teams ");

        if (!ValidateEmail(mail)) throw new Exception($"Invalid email: {mail}");

        var subscriptionContainer = await GetEndpointContainer(SubscriptionsContainer);
        var subscription = new EndpointSubscription
        {
            Mail = mail,
            Url = url,
            Type = formattedType,
            EndpointId = endpointId,
            AuthorId = author,
            Id = Guid.NewGuid().ToString(),
            EventTypes = eventTypes,
            Payload = payload,
            Frequency = frequency
        };

        //Add author here
        var response = await subscriptionContainer.UpsertItemAsync(subscription, new PartitionKey(subscription.Id));

        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created)
        {
            _logger?.LogTrace(
                "COSMOS SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscription.EndpointId, subscription.Id, response.StatusCode);
            return subscription;
        }

        _logger?.LogError(
            "COSMOS SUBSCRIPTION ERROR: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscription.EndpointId, subscription.Id, response.StatusCode);
        return null; //Return error?
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
    {
        var subscriptions = new List<EndpointSubscription>();
        var subscriptionContainer = await GetSubscriptionsContainer();

        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.endpointId = @endpointId")
            .WithParameter("@endpointId", endpointId);
        var result = subscriptionContainer.GetItemQueryIterator<EndpointSubscription>(queryDefinition);

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                subscriptions.Add(queryResult);
            }
        }

        return subscriptions;
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpointId,
        string eventType, string payload, string errorText)
    {
        var subscriptions = new List<EndpointSubscription>();
        var subscriptionContainer = await GetSubscriptionsContainer();

        var sqlQuery = "SELECT * FROM c WHERE c.endpointId = @endpointId";

        // Build query dynamically with parameterized values
        if (!String.IsNullOrEmpty(eventType))
        {
            sqlQuery += " AND (ARRAY_CONTAINS(c.eventTypes, @eventType) OR ARRAY_LENGTH(c.eventTypes) = 0 OR c.eventTypes = null OR c.eventTypes = '' OR (NOT IS_DEFINED(c.eventTypes)))";
        }
        if (!String.IsNullOrEmpty(payload))
        {
            sqlQuery += " AND (CONTAINS(@payload, c.payload) OR c.payload = null OR c.payload = '' OR (NOT IS_DEFINED(c.payload))";
            if (!String.IsNullOrEmpty(errorText))
            {
                sqlQuery += " OR CONTAINS(@errorText, c.payload)";
            }
            sqlQuery += ")";
        }

        var queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@endpointId", endpointId);

        if (!String.IsNullOrEmpty(eventType))
            queryDefinition = queryDefinition.WithParameter("@eventType", eventType);
        if (!String.IsNullOrEmpty(payload))
            queryDefinition = queryDefinition.WithParameter("@payload", payload);
        if (!String.IsNullOrEmpty(errorText))
            queryDefinition = queryDefinition.WithParameter("@errorText", errorText);

        var result = subscriptionContainer.GetItemQueryIterator<EndpointSubscription>(queryDefinition);

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                subscriptions.Add(queryResult);
            }
        }

        return subscriptions;
    }

    public async Task<bool> DeleteSubscription(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)) return false;

        var subscriptionContainer = await GetEndpointContainer(SubscriptionsContainer);

        try
        {
            var response = await subscriptionContainer.DeleteItemAsync<SubscriptionDbo>(subscriptionId, new PartitionKey(subscriptionId));
            _logger?.LogTrace(
                "COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscriptionId, response.StatusCode);
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {SubscriptionId}", subscriptionId);
            return false;
        }
    }
    public async Task<bool> UnsubscribeById(string endpointId, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var subscriptionContainer = await GetEndpointContainer(SubscriptionsContainer);

        try
        {
            var response = await subscriptionContainer.DeleteItemAsync<SubscriptionDbo>(id, new PartitionKey(id));
            _logger?.LogTrace(
                "COSMOS REMOVE-SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", endpointId, id, response.StatusCode);
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}", endpointId, id);
            return false;
        }
    }

    public async Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        if (string.IsNullOrWhiteSpace(mail)) return false;

        var subs = await GetSubscriptionsOnEndpoint(endpointId);
        var mySubscription =
            subs.FirstOrDefault(x => string.Equals(mail, x.Mail, StringComparison.OrdinalIgnoreCase));
        if (mySubscription != null)
        {
            return await UnsubscribeById(endpointId, mySubscription.Id);
        }

        return false;
    }

    public async Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        subscription.ErrorList = await GetEndpointErrorList(subscription.EndpointId);
        subscription.NotifiedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        try
        {
            var subscriberContainer = await GetEndpointContainer(SubscriptionsContainer);
            await subscriberContainer.UpsertItemAsync(subscription, new PartitionKey(subscription.Id));
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS UPDATE-SUBSCRIPTION: Endpoint: {EndpointId}, SubscriptionId: {SubscriptionId}", subscription.EndpointId, subscription.Id);
            return false;
        }
    }

    public async Task<string> GetEndpointErrorList(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        // Only the id is used by the caller — project it server-side instead of
        // reading whole EventDbo documents (large EventJson / stack traces), which
        // cut RU and payload by 10-50x. Accumulate then join once (no O(n^2) concat).
        var queryDefinition = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.status IN (@failedStatus, @deferredStatus) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@failedStatus", FailedStatus)
            .WithParameter("@deferredStatus", DeferredStatus);

        var result = container.GetItemQueryIterator<IdProjection>(queryDefinition);
        var ids = new List<string>();
        while (result.HasMoreResults)
        {
            var message = await result.ReadNextAsync();
            foreach (var queryResult in message)
            {
                ids.Add(queryResult.Id);
            }
        }

        // Preserve the historical "id1;id2;...;" shape (trailing separator included).
        return ids.Count == 0 ? "" : string.Join(";", ids) + ";";
    }

    private sealed class IdProjection
    {
        [JsonProperty("id")] public string Id { get; set; }
    }

    /// <summary>
    /// Server-side projection of the four fields that feed <see cref="BlockedMessageEvent"/>.
    /// </summary>
    private sealed class BlockedEventProjection
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("originatingMessageId")] public string OriginatingMessageId { get; set; }
        [JsonProperty("lastMessageId")] public string LastMessageId { get; set; }
    }

    private static BlockedMessageEvent ToBlockedMessageEvent(BlockedEventProjection projection) => new()
    {
        EventId = projection.EventId,
        OriginatingId = projection.OriginatingMessageId.Equals("self", StringComparison.OrdinalIgnoreCase)
            ? projection.LastMessageId
            : projection.OriginatingMessageId,
        Status = projection.Status,
    };

    // Resolves a container handle through _containerCache, running "ensure
    // exists" exactly once per container id and caching the resulting handle.
    // A faulted creation is never left cached: the faulted entry is evicted
    // (key + value match, so a newer good entry is never removed) and the
    // next caller retries.
    private async Task<ICosmosContainerAdapter> GetCachedContainerAsync(string containerId, string partitionKeyPath)
    {
        var containerTask = _containerCache.GetOrAdd(containerId, id => EnsureContainerExistsAsync(id, partitionKeyPath));
        try
        {
            return await containerTask;
        }
        catch
        {
            _containerCache.TryRemove(new KeyValuePair<string, Task<ICosmosContainerAdapter>>(containerId, containerTask));
            throw;
        }
    }

    private async Task<ICosmosContainerAdapter> EnsureContainerExistsAsync(string containerId, string partitionKeyPath)
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await CosmosExceptionTranslation.TranslateTransientAsync(
            () => db.CreateContainerIfNotExistsAsync(containerId, partitionKeyPath),
            _logger);
    }

    private Task<ICosmosContainerAdapter> GetEndpointContainer(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
        {
            throw new ArgumentNullException(nameof(endpointId), "EndpointId cannot be null or empty");
        }
        return GetCachedContainerAsync(endpointId, "/id");
    }

    private Task<ICosmosContainerAdapter> GetSubscriptionsContainer() =>
        GetCachedContainerAsync(SubscriptionsContainer, "/id");

    private Task<ICosmosContainerAdapter> GetMessagesContainer() =>
        GetCachedContainerAsync(MessagesContainer, "/eventId");

    private Task<ICosmosContainerAdapter> GetAuditsContainer() =>
        GetCachedContainerAsync(AuditsContainer, "/eventId");

    private Task<ICosmosContainerAdapter> GetEventSchemasContainer() =>
        GetCachedContainerAsync(EventSchemasContainer, "/id");

    // ── IEventSchemaStore ──────────────────────────────────────────────────────

    public async Task<EventSchema?> GetSchema(string eventTypeId)
    {
        var container = await GetEventSchemasContainer();
        try
        {
            var resp = await container.ReadItemAsync<EventSchema>(eventTypeId, new PartitionKey(eventTypeId));
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EventSchema>> GetSchemas()
    {
        var container = await GetEventSchemasContainer();
        var results = new List<EventSchema>();
        using var iterator = container.GetItemQueryIterator<EventSchema>("SELECT * FROM c");
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    public async Task<EventSchema> DefineEventType(EventSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema?.EventTypeId))
            throw new ArgumentException("schema.EventTypeId is required.", nameof(schema));
        if (string.IsNullOrWhiteSpace(schema?.JsonSchema))
            throw new ArgumentException("schema.JsonSchema is required.", nameof(schema));

        var existing = await GetSchema(schema.EventTypeId);
        if (existing != null)
        {
            if (!SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return existing;
        }

        var container = await GetEventSchemasContainer();
        try
        {
            // Atomic create-or-409: never an upsert, so a concurrent create of a
            // DIFFERENT schema for the same new id can't silently overwrite.
            var resp = await container.CreateItemAsync(schema, new PartitionKey(schema.EventTypeId));
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Lost the create race. Re-read the winner; surface a conflict only if it
            // differs (schemas are immutable), otherwise the create was idempotent.
            var raced = await GetSchema(schema.EventTypeId);
            if (raced is null)
                throw new InvalidOperationException(
                    $"Event type '{schema.EventTypeId}' reported a create conflict but could not be re-read.");
            if (!SchemaJson.Equal(raced.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return raced;
        }
    }

    /// <summary>
    /// Projection for <see cref="SearchMessages"/>: every <see cref="MessageEntity"/>
    /// property EXCEPT the heavy <c>EventContent.EventJson</c> payload (the list
    /// and palette views never read it; detail views fetch it on demand via
    /// <see cref="GetMessage"/> / <see cref="GetLatestEventRequestMessage"/>).
    /// <c>ErrorContent</c> is projected whole. <c>From</c>/<c>To</c> need bracket
    /// notation (reserved keywords). Drift guard: a unit test reflects over
    /// <see cref="MessageEntity"/>'s properties and fails when a new property is
    /// missing from this string — extend it when adding properties.
    /// </summary>
    internal const string MessageSearchProjection =
        "SELECT c.id, c.eventId, c.endpointId, " +
        "{" +
        "\"EventId\": c.message.EventId, " +
        "\"MessageId\": c.message.MessageId, " +
        "\"EventTypeId\": c.message.EventTypeId, " +
        "\"OriginatingMessageId\": c.message.OriginatingMessageId, " +
        "\"ParentMessageId\": c.message.ParentMessageId, " +
        "\"From\": c.message[\"From\"], " +
        "\"To\": c.message[\"To\"], " +
        "\"OriginatingFrom\": c.message.OriginatingFrom, " +
        "\"SessionId\": c.message.SessionId, " +
        "\"CorrelationId\": c.message.CorrelationId, " +
        "\"EnqueuedTimeUtc\": c.message.EnqueuedTimeUtc, " +
        "\"MessageContent\": {" +
        "\"EventContent\": {\"EventTypeId\": c.message.MessageContent.EventContent.EventTypeId}, " +
        "\"ErrorContent\": c.message.MessageContent.ErrorContent" +
        "}, " +
        "\"MessageType\": c.message.MessageType, " +
        "\"EndpointRole\": c.message.EndpointRole, " +
        "\"EndpointId\": c.message.EndpointId, " +
        "\"RetryCount\": c.message.RetryCount, " +
        "\"RetryLimit\": c.message.RetryLimit, " +
        "\"DeadLetterReason\": c.message.DeadLetterReason, " +
        "\"DeadLetterErrorDescription\": c.message.DeadLetterErrorDescription, " +
        "\"OriginalSessionId\": c.message.OriginalSessionId, " +
        "\"DeferralSequence\": c.message.DeferralSequence, " +
        "\"QueueTimeMs\": c.message.QueueTimeMs, " +
        "\"ProcessingTimeMs\": c.message.ProcessingTimeMs, " +
        "\"PendingSubStatus\": c.message.PendingSubStatus, " +
        "\"HandoffReason\": c.message.HandoffReason, " +
        "\"ExternalJobId\": c.message.ExternalJobId, " +
        "\"ExpectedBy\": c.message.ExpectedBy, " +
        "\"CloudEventId\": c.message.CloudEventId, " +
        "\"CloudEventSource\": c.message.CloudEventSource, " +
        "\"CloudEventType\": c.message.CloudEventType, " +
        "\"CloudEventSubject\": c.message.CloudEventSubject" +
        "} AS message FROM c";

    public async Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        var container = await GetMessagesContainer();
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();
        var paramIndex = 0;

        string NextParam() => $"@p{paramIndex++}";

        // ID-like fields use case-insensitive PREFIX matching: STARTSWITH with
        // ignoreCase can be served from the range index, whereas CONTAINS (and
        // any LOWER() wrapper) forces a full scan of the container. Free-text
        // fields (From/To below) keep CONTAINS.
        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.endpointId, {p}, true)");
            parameters[p] = filter.EndpointId;
        }

        if (!string.IsNullOrEmpty(filter.EventId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.eventId, {p}, true)");
            parameters[p] = filter.EventId;
        }

        if (!string.IsNullOrEmpty(filter.MessageId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.id, {p}, true)");
            parameters[p] = filter.MessageId;
        }

        if (!string.IsNullOrEmpty(filter.SessionId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.message.SessionId, {p}, true)");
            parameters[p] = filter.SessionId;
        }

        if (filter.EventTypeId != null && filter.EventTypeId.Any())
        {
            var p = NextParam();
            conditions.Add($"ARRAY_CONTAINS({p}, c.message.EventTypeId)");
            parameters[p] = filter.EventTypeId;
        }

        if (!string.IsNullOrEmpty(filter.From))
        {
            var p = NextParam();
            // "From" is a reserved keyword in Cosmos DB SQL — must use bracket notation
            conditions.Add($"CONTAINS(c.message[\"From\"], {p}, true)");
            parameters[p] = filter.From;
        }

        if (!string.IsNullOrEmpty(filter.To))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(c.message[\"To\"], {p}, true)");
            parameters[p] = filter.To;
        }

        if (filter.MessageType != null)
        {
            var p = NextParam();
            conditions.Add($"c.message.MessageType = {p}");
            parameters[p] = filter.MessageType.ToString();
        }

        if (filter.EnqueuedAtFrom != null)
        {
            var p = NextParam();
            conditions.Add($"c.message.EnqueuedTimeUtc >= {p}");
            parameters[p] = filter.EnqueuedAtFrom.Value;
        }

        if (filter.EnqueuedAtTo != null)
        {
            var p = NextParam();
            conditions.Add($"c.message.EnqueuedTimeUtc <= {p}");
            parameters[p] = filter.EnqueuedAtTo.Value;
        }

        var sql = MessageSearchProjection;
        if (conditions.Any())
            sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY c.message.EnqueuedTimeUtc DESC";

        var queryDef = new QueryDefinition(sql);
        foreach (var kvp in parameters)
            queryDef = queryDef.WithParameter(kvp.Key, kvp.Value);

        var requestOptions = new QueryRequestOptions { MaxItemCount = PaginationLimits.Resolve(maxItemCount) };
        var result = container.GetItemQueryIterator<MessageDocument>(
            queryDef,
            string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            requestOptions);

        var messages = new List<MessageEntity>();
        string? token = null;

        if (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            token = feed.ContinuationToken;
            foreach (var doc in feed)
            {
                messages.Add(doc.Message);
            }
        }

        return new MessageSearchResult { Messages = messages, ContinuationToken = token };
    }

    public async Task StoreMessage(MessageEntity message)
    {
        var container = await GetMessagesContainer();
        var doc = new MessageDocument
        {
            Id = message.MessageId,
            EventId = message.EventId,
            EndpointId = message.EndpointId,
            Message = message,
            TimeToLive = 60 * 60 * 24 * 90 // 90-day TTL
        };

        try
        {
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId), SuppressContentOnWrite);
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e, "COSMOS STORE-MESSAGE-ERROR: EventId: {EventId}, MessageId: {MessageId}", message.EventId, message.MessageId);
            throw;
        }
    }

    public async Task RemoveStoredMessage(string eventId, string messageId)
    {
        var container = await GetMessagesContainer();
        try
        {
            await container.DeleteItemAsync<MessageDocument>(messageId, new PartitionKey(eventId));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted, ignore
        }
    }

    public async Task<MessageEntity> GetMessage(string eventId, string messageId)
    {
        var container = await GetMessagesContainer();
        try
        {
            var response = await container.ReadItemAsync<MessageDocument>(messageId, new PartitionKey(eventId));
            return response.Resource?.Message;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
    {
        var container = await GetMessagesContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId")
            .WithParameter("@eventId", eventId);
        // Bound page size so an event with a long history streams in pages
        // instead of one oversized response (documents carry the full EventJson
        // payload). The loop still drains every match.
        var result = container.GetItemQueryIterator<MessageDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 100 });
        var messages = new List<MessageEntity>();

        while (result.HasMoreResults)
        {
            var feed = await CosmosExceptionTranslation.TranslateTransientAsync(
                () => result.ReadNextAsync(),
                _logger);
            foreach (var doc in feed)
            {
                messages.Add(doc.Message);
            }
        }

        return messages;
    }

    public async Task<MessageEntity> GetLatestEventRequestMessage(string eventId)
    {
        var container = await GetMessagesContainer();
        // Single-partition (the messages container is partitioned by /eventId) TOP 1:
        // fetch only the newest message that carries event content instead of pulling
        // the whole history and filtering in memory on every event-detail page load.
        var query = new QueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.eventId = @eventId " +
                "AND c.message.MessageType IN ('EventRequest', 'ResubmissionRequest') " +
                "AND IS_DEFINED(c.message.MessageContent.EventContent.EventJson) " +
                "AND c.message.MessageContent.EventContent.EventJson != null " +
                "AND c.message.MessageContent.EventContent.EventJson != '' " +
                "ORDER BY c.message.EnqueuedTimeUtc DESC")
            .WithParameter("@eventId", eventId);
        var result = container.GetItemQueryIterator<MessageDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 1 });
        if (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            return feed.FirstOrDefault()?.Message;
        }

        return null;
    }

    public async Task<MessageEntity> GetFailedMessage(string eventId, string endpointId)
    {
        var messages = await GetMessagesByEventAndEndpoint(eventId, endpointId);

        return messages
            .Where(me => me.MessageContent?.ErrorContent != null)
            .OrderBy(me => me.EnqueuedTimeUtc)
            .LastOrDefault();
    }

    public async Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId)
    {
        var messages = await GetMessagesByEventAndEndpoint(eventId, endpointId);

        return messages
            .OrderBy(me => me.EnqueuedTimeUtc)
            .LastOrDefault();
    }

    private async Task<List<MessageEntity>> GetMessagesByEventAndEndpoint(string eventId, string endpointId)
    {
        var container = await GetMessagesContainer();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.eventId = @eventId AND LOWER(c.endpointId) = LOWER(@endpointId)")
            .WithParameter("@eventId", eventId)
            .WithParameter("@endpointId", endpointId);
        var result = container.GetItemQueryIterator<MessageDocument>(query);
        var messages = new List<MessageEntity>();

        while (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            foreach (var doc in feed)
            {
                messages.Add(doc.Message);
            }
        }

        return messages;
    }

    public async Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        var container = await GetAuditsContainer();
        var doc = new AuditDocument
        {
            Id = Guid.NewGuid().ToString(),
            EventId = eventId,
            EndpointId = endpointId,
            EventTypeId = eventTypeId,
            Audit = auditEntity,
            CreatedAt = DateTime.UtcNow,
            TimeToLive = 60 * 60 * 24 * 365 // 1-year TTL
        };

        try
        {
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId), SuppressContentOnWrite);
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e, "COSMOS STORE-AUDIT-ERROR: EventId: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        var container = await GetAuditsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId ORDER BY c.createdAt DESC")
            .WithParameter("@eventId", eventId);
        // Bound page size so heavily-audited events stream in pages rather than
        // one huge response. The loop still drains every match.
        var result = container.GetItemQueryIterator<AuditDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 1000 });
        var audits = new List<MessageAuditEntity>();

        while (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            foreach (var doc in feed)
            {
                audits.Add(doc.Audit);
            }
        }

        return audits;
    }

    public async Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var container = await GetAuditsContainer();
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();
        var paramIndex = 0;

        string NextParam() => $"@p{paramIndex++}";

        // Case-insensitive PREFIX matching — index-served, unlike CONTAINS/LOWER
        // which force a full container scan. See SearchMessages.
        if (!string.IsNullOrEmpty(filter.EventId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.eventId, {p}, true)");
            parameters[p] = filter.EventId;
        }

        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.endpointId, {p}, true)");
            parameters[p] = filter.EndpointId;
        }

        if (!string.IsNullOrEmpty(filter.AuditorName))
        {
            var p = NextParam();
            // MessageAuditEntity serializes with PascalCase property names (no
            // [JsonProperty] attributes) - the path is c.audit.AuditorName; the
            // previous lowercase path never matched anything on Cosmos.
            conditions.Add($"STARTSWITH(c.audit.AuditorName, {p}, true)");
            parameters[p] = filter.AuditorName;
        }

        if (!string.IsNullOrEmpty(filter.EventTypeId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.eventTypeId, {p}, true)");
            parameters[p] = filter.EventTypeId;
        }

        if (filter.AuditType != null)
        {
            var p = NextParam();
            // PascalCase path (see AuditorName above); the enum serializes as its
            // NUMERIC value (no StringEnumConverter on MessageAuditType), so the
            // comparison must be numeric too - the previous string compare never
            // matched anything on Cosmos.
            conditions.Add($"c.audit.AuditType = {p}");
            parameters[p] = (int)filter.AuditType.Value;
        }

        if (filter.CreatedAtFrom != null)
        {
            var p = NextParam();
            conditions.Add($"c.createdAt >= {p}");
            parameters[p] = filter.CreatedAtFrom.Value;
        }

        if (filter.CreatedAtTo != null)
        {
            var p = NextParam();
            conditions.Add($"c.createdAt <= {p}");
            parameters[p] = filter.CreatedAtTo.Value;
        }

        var sql = "SELECT * FROM c";
        if (conditions.Any())
            sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY c.createdAt DESC";

        var queryDef = new QueryDefinition(sql);
        foreach (var kvp in parameters)
            queryDef = queryDef.WithParameter(kvp.Key, kvp.Value);

        var requestOptions = new QueryRequestOptions { MaxItemCount = PaginationLimits.Resolve(maxItemCount) };
        var result = container.GetItemQueryIterator<AuditDocument>(
            queryDef,
            string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            requestOptions);

        var audits = new List<AuditSearchItem>();
        string? token = null;

        if (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            token = feed.ContinuationToken;
            foreach (var doc in feed)
            {
                audits.Add(new AuditSearchItem
                {
                    EventId = doc.EventId,
                    EndpointId = doc.EndpointId,
                    EventTypeId = doc.EventTypeId,
                    Audit = doc.Audit,
                    CreatedAt = doc.CreatedAt
                });
            }
        }

        return new AuditSearchResult { Audits = audits, ContinuationToken = token };
    }

    public async Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var counts = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return counts;

        // Document-level ids are camelCase ([JsonProperty] on AuditDocument);
        // the nested audit entity serializes with PascalCase names and a NUMERIC
        // AuditType (no attributes / no StringEnumConverter) — see SearchAudits.
        // AccessDenied=false-or-undefined excludes denied resubmit attempts while
        // keeping legacy documents (written before the field existed) counted.
        // The audits container is partitioned by /eventId, so this GROUP BY is
        // cross-partition — but bounded by the explicit event-id list, it stays a
        // single cheap fan-out instead of one round-trip per row on the page.
        var resubmitTypes = new[]
        {
            (int)MessageAuditType.Resubmit,
            (int)MessageAuditType.ResubmitWithChanges,
        };

        var query = new QueryDefinition(
                "SELECT c.eventId AS EventId, COUNT(1) AS Count FROM c " +
                "WHERE c.endpointId = @endpointId " +
                "AND ARRAY_CONTAINS(@types, c.audit.AuditType) " +
                "AND ARRAY_CONTAINS(@eventIds, c.eventId) " +
                "AND (NOT IS_DEFINED(c.audit.AccessDenied) OR c.audit.AccessDenied = false) " +
                "GROUP BY c.eventId")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@types", resubmitTypes)
            .WithParameter("@eventIds", ids);

        var container = await GetAuditsContainer();
        var iterator = container.GetItemQueryIterator<AuditCountRow>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var row in await iterator.ReadNextAsync())
            {
                if (!string.IsNullOrEmpty(row.EventId))
                    counts[row.EventId] = row.Count;
            }
        }

        return counts;
    }

    private sealed class AuditCountRow
    {
        public string EventId { get; set; }

        public int Count { get; set; }
    }

    public async Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            await container.PatchItemAsync<EventDbo>(id, new PartitionKey(id), new[]
            {
                PatchOperation.Set("/deleted", true),
                PatchOperation.Set("/ttl", 60 * 60 * 24 * 30) // 30-day TTL
            });
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogTrace("COSMOS ARCHIVE-FAILED: Event not found. EventId: {EventId}, SessionId: {SessionId}, EndpointId: {EndpointId}", eventId, sessionId, endpointId);
        }
    }

    private async Task<bool> UploadMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content, string status)
    {
        var container = await GetEndpointContainer(endpointId);

        var eventDbo = new EventDbo
        {
            Id = $"{eventId}_{sessionId}",
            Event = content,
            SessionId = sessionId,
            Status = status,
            EventType = content.EventTypeId,
            Deleted = false,
            TimeToLive = -1 // TTL Disabled
        };

        try
        {
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id), SuppressContentOnWrite);
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}, Status: {Status}", eventId, sessionId, response.StatusCode, status);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: EventId: {EventId}, SessionId: {SessionId}, Status: {Status}, HttpStatusCode: {StatusCode}", eventId, sessionId, status, e.StatusCode);
            throw;
        }
    }

    private static bool ValidateEmail(string mail)
    {
        var regex = new Regex(@"^([\w\.\-]+)@([\w\-]+)((\.(\w){2,3})+)$");
        var match = regex.Match(mail);
        return match.Success;
    }

    public async Task<EndpointMetadata> GetEndpointMetadata(string endpointId)
    {
        var container = await GetEndpointContainer("Metadata");
        try
        {
            var rel = await container.ReadItemAsync<EndpointMetadata>(endpointId, new PartitionKey(endpointId));
            return rel.Resource;
        }
        catch (CosmosException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            throw;
        }
    }

    public async Task<List<EndpointMetadata>>? GetMetadatas(IEnumerable<string> endpointIds)
    {
        var container = await GetEndpointContainer("Metadata");
        try
        {
            var rel = await container.ReadManyItemsAsync<EndpointMetadata>(endpointIds.Select(x => (x, new PartitionKey(x))).ToList());
            return rel.Any() ? rel.Resource.ToList() : null;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("COSMOS METADATAS: Some endpoints not found in metadata container");
            return null;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS METADATAS-ERROR: Failed to get metadatas for endpoints");
            throw;
        }
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        var sqlQuery = "SELECT * FROM c";
        var metadatas = await GetMetadatasByFilter(sqlQuery);

        return metadatas;
    }

    private async Task<List<EndpointMetadata>> GetMetadatasByFilter(string sqlQuery)
    {
        var container = await GetEndpointContainer("Metadata");
        var result = container.GetItemQueryIterator<EndpointMetadata>(sqlQuery);
        var metadatas = new List<EndpointMetadata>();

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                metadatas.Add(queryResult);
            }
        }
        return metadatas;
    }

    public async Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var container = await GetEndpointContainer("Metadata");

        try
        {
            var response =
                await container.UpsertItemAsync(endpointMetadata, new PartitionKey(endpointMetadata.EndpointId));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointMetadata.EndpointId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointMetadata.EndpointId, e.StatusCode);
            throw;
        }
    }

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
    {
        var container = await GetMessagesContainer();
        var fromIso = from.ToString("o");

        // The three aggregates are independent — run them concurrently so the
        // endpoint overview costs one round-trip's latency instead of three.
        var publishedTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.message[\"From\"] AS endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.message[\"From\"], c.message.EventTypeId",
            fromIso);

        var failedTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        var handledTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        await Task.WhenAll(publishedTask, failedTask, handledTask);

        return new EndpointMetricsResult
        {
            Published = await publishedTask,
            Handled = await handledTask,
            Failed = await failedTask
        };
    }

    public async Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from)
    {
        // Server-side aggregation over all ResolutionResponse / ErrorResponse
        // documents in the period. Two queries (one per timing series) so we
        // can WHERE-out null values cleanly — the GROUP BY keys must align
        // across the two so we can stitch them back together.
        // Picking only outcome documents avoids double-counting (the original
        // EventRequest doesn't carry timings; only the response does).
        var container = await GetMessagesContainer();
        var fromIso = from.ToString("o");
        var outcomeFilter =
            "(c.message.MessageType = 'ResolutionResponse' OR " +
            " c.message.MessageType = 'ErrorResponse' OR " +
            " c.message.MessageType = 'SkipResponse' OR " +
            " c.message.MessageType = 'DeferralResponse' OR " +
            " c.message.MessageType = 'UnsupportedResponse')";

        // Queue-time and processing-time aggregates are independent — run them
        // concurrently and stitch the results below.
        var queueRowsTask = RunLatencyAggregateQuery(container,
            "SELECT c.endpointId, c.message.EventTypeId, " +
            "       COUNT(1) AS count, " +
            "       AVG(c.message.QueueTimeMs) AS avg, " +
            "       MIN(c.message.QueueTimeMs) AS min, " +
            "       MAX(c.message.QueueTimeMs) AS max " +
            "FROM c " +
            $"WHERE {outcomeFilter} " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "AND IS_DEFINED(c.message.QueueTimeMs) " +
            "AND c.message.QueueTimeMs != null " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        var processingRowsTask = RunLatencyAggregateQuery(container,
            "SELECT c.endpointId, c.message.EventTypeId, " +
            "       COUNT(1) AS count, " +
            "       AVG(c.message.ProcessingTimeMs) AS avg, " +
            "       MIN(c.message.ProcessingTimeMs) AS min, " +
            "       MAX(c.message.ProcessingTimeMs) AS max " +
            "FROM c " +
            $"WHERE {outcomeFilter} " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "AND IS_DEFINED(c.message.ProcessingTimeMs) " +
            "AND c.message.ProcessingTimeMs != null " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        await Task.WhenAll(queueRowsTask, processingRowsTask);
        var queueRows = await queueRowsTask;
        var processingRows = await processingRowsTask;

        // Merge by (endpointId, eventTypeId). One side may be missing rows
        // (e.g. processing time not captured pre-fix); leaves that side at
        // its default zeroed aggregate.
        var grouped = new Dictionary<(string Endpoint, string EventType), EndpointLatencyAggregate>();
        foreach (var row in queueRows)
        {
            var key = (row.EndpointId ?? string.Empty, row.EventTypeId ?? string.Empty);
            if (!grouped.TryGetValue(key, out var agg))
            {
                agg = new EndpointLatencyAggregate { EndpointId = key.Item1, EventTypeId = key.Item2 };
                grouped[key] = agg;
            }
            agg.Queue = new LatencyAggregate { Count = row.Count, AvgMs = row.Avg, MinMs = row.Min, MaxMs = row.Max };
        }
        foreach (var row in processingRows)
        {
            var key = (row.EndpointId ?? string.Empty, row.EventTypeId ?? string.Empty);
            if (!grouped.TryGetValue(key, out var agg))
            {
                agg = new EndpointLatencyAggregate { EndpointId = key.Item1, EventTypeId = key.Item2 };
                grouped[key] = agg;
            }
            agg.Processing = new LatencyAggregate { Count = row.Count, AvgMs = row.Avg, MinMs = row.Min, MaxMs = row.Max };
        }

        return new EndpointLatencyMetricsResult { Latencies = grouped.Values.ToList() };
    }

    private async Task<List<LatencyAggregateRow>> RunLatencyAggregateQuery(ICosmosContainerAdapter container, string sql, string fromIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso);
        var iterator = container.GetItemQueryIterator<LatencyAggregateRow>(query);
        var results = new List<LatencyAggregateRow>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            foreach (var row in page) results.Add(row);
        }
        return results;
    }

    private sealed class LatencyAggregateRow
    {
        [JsonProperty("endpointId")] public string EndpointId { get; set; }
        [JsonProperty("EventTypeId")] public string EventTypeId { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("avg")] public double Avg { get; set; }
        [JsonProperty("min")] public double Min { get; set; }
        [JsonProperty("max")] public double Max { get; set; }
    }

    public async Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from)
    {
        var container = await GetMessagesContainer();
        var fromIso = from.ToString("o");

        var sql = "SELECT c.endpointId, c.message.EventTypeId, " +
                  "c.message.MessageContent.ErrorContent.ErrorText, " +
                  "c.message.EnqueuedTimeUtc, c.message.EventId " +
                  "FROM c " +
                  "WHERE c.message.MessageType = 'ErrorResponse' " +
                  "AND c.message.EnqueuedTimeUtc >= @from";

        var query = new QueryDefinition(sql).WithParameter("@from", fromIso);
        // Bound page size so a high-failure window streams in pages instead of
        // materialising one huge response (each row already projects just the
        // fields below, never the full document). The loop still drains every match.
        var iterator = container.GetItemQueryIterator<FailedMessageQueryResult>(query, null,
            new QueryRequestOptions { MaxItemCount = 1000 });
        var results = new List<FailedMessageInfo>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                results.Add(new FailedMessageInfo
                {
                    EndpointId = item.EndpointId,
                    EventTypeId = item.EventTypeId,
                    ErrorText = item.ErrorText,
                    EnqueuedTimeUtc = item.EnqueuedTimeUtc,
                    EventId = item.EventId
                });
            }
        }

        return results;
    }

    public async Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
    {
        var container = await GetMessagesContainer();
        var fromIso = from.ToString("o");

        // The three bucket aggregates are independent — run them concurrently.
        var publishedBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

        var handledBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

        var failedBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

        await Task.WhenAll(publishedBucketsTask, handledBucketsTask, failedBucketsTask);
        var publishedBuckets = await publishedBucketsTask;
        var handledBuckets = await handledBucketsTask;
        var failedBuckets = await failedBucketsTask;

        var allBucketKeys = GenerateBucketKeys(from, DateTime.UtcNow, substringLength)
            .Concat(publishedBuckets.Keys)
            .Concat(handledBuckets.Keys)
            .Concat(failedBuckets.Keys)
            .Distinct()
            .OrderBy(k => k);

        var dataPoints = allBucketKeys.Select(key => new TimeSeriesBucket
        {
            Timestamp = key,
            Published = publishedBuckets.GetValueOrDefault(key),
            Handled = handledBuckets.GetValueOrDefault(key),
            Failed = failedBuckets.GetValueOrDefault(key)
        }).ToList();

        return new TimeSeriesResult { BucketSize = bucketLabel, DataPoints = dataPoints };
    }

    private async Task<Dictionary<string, int>> RunBucketCountQuery(ICosmosContainerAdapter container, string sql, string fromIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso);
        var iterator = container.GetItemQueryIterator<BucketCountResult>(query);
        var results = new Dictionary<string, int>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                if (item.Bucket != null)
                    results[item.Bucket] = item.Count;
            }
        }

        return results;
    }

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
        var keys = new List<string>();
        var current = substringLength switch
        {
            16 => new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, DateTimeKind.Utc),
            13 => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc),
            10 => new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc)
        };

        var step = substringLength switch
        {
            16 => TimeSpan.FromMinutes(1),
            13 => TimeSpan.FromHours(1),
            10 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

        while (current <= to)
        {
            var key = current.ToString("o")[..substringLength];
            keys.Add(key);
            current += step;
        }

        return keys;
    }

    private async Task<List<EndpointEventTypeCount>> RunEventTypeCountQuery(ICosmosContainerAdapter container, string sql, string fromIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso);
        var iterator = container.GetItemQueryIterator<MetricsEventTypeCountResult>(query);
        var results = new List<EndpointEventTypeCount>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                results.Add(new EndpointEventTypeCount
                {
                    EndpointId = item.EndpointId,
                    EventTypeId = item.EventTypeId,
                    Count = item.Count
                });
            }
        }

        return results;
    }

    class FailedMessageQueryResult
    {
        [JsonProperty("endpointId")]
        public string EndpointId { get; set; }

        [JsonProperty("EventTypeId")]
        public string EventTypeId { get; set; }

        [JsonProperty("ErrorText")]
        public string ErrorText { get; set; }

        [JsonProperty("EnqueuedTimeUtc")]
        public DateTime EnqueuedTimeUtc { get; set; }

        [JsonProperty("EventId")]
        public string EventId { get; set; }
    }

    class BucketCountResult
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("bucket")]
        public string Bucket { get; set; }
    }

    class MetricsEventTypeCountResult
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("endpointId")]
        public string EndpointId { get; set; }

        [JsonProperty("EventTypeId")]
        public string EventTypeId { get; set; }
    }

    class StatusQueryResult
    {
        public int EventCount { get; set; }

        [JsonProperty(PropertyName = "Status")]
        public string Status { get; set; }
    }

    class SessionCountQueryResult
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; }

        [JsonProperty(PropertyName = "Status")]
        public string Status { get; set; }
    }

    class BatchSessionQueryResult
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; }

        [JsonProperty(PropertyName = "status")]
        public string Status { get; set; }

        [JsonProperty(PropertyName = "sessionId")]
        public string SessionId { get; set; }
    }

    class EventDbo
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; }

        [JsonProperty(PropertyName = "status")]
        public string Status { get; set; }

        [JsonProperty(PropertyName = "eventType")]
        public string EventType { get; set; }

        [JsonProperty(PropertyName = "sessionId")]
        public string SessionId { get; set; }

        [JsonProperty(PropertyName = "event")] public UnresolvedEvent Event { get; set; }

        [JsonProperty(PropertyName = "deleted")]
        public bool? Deleted { get; set; }

        [JsonProperty(PropertyName = "ttl", NullValueHandling = NullValueHandling.Ignore)]
        public int? TimeToLive { get; set; }
    }

    class SubscriptionDbo
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; }
        [JsonProperty(PropertyName = "type")] public string Type { get; set; }

        [JsonProperty(PropertyName = "severity")]
        public string Severity { get; set; }

        [JsonProperty(PropertyName = "mail")] public string Mail { get; set; }

        [JsonProperty(PropertyName = "endpointId")]
        public string EndpointId { get; set; }
    }
}
