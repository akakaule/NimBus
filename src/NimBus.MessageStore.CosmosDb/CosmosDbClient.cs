using Serilog;
using NimBus.Core.Messages;
using NimBus.MessageStore.States;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
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
    Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat();
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);
    Task EnableHeartbeatOnEndpoint(string endpointId, bool enable);

    Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId);

    // Message search (cross-partition query on messages container)
    Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount);

    // Message history (replaces IMessageStoreClient blob operations)
    Task StoreMessage(MessageEntity message);
    Task<MessageEntity> GetMessage(string eventId, string messageId);
    Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId);
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
    private const string DatabaseId = "MessageDatabase";

    private const string PendingStatus = "Pending";
    private const string FailedStatus = "Failed";
    private const string DeferredStatus = "Deferred";
    private const string DLQStatus = "DeadLettered";
    private const string UnsupportedStatus = "Unsupported";
    private const string CompletedStatus = "Completed";
    private const string SkippedStatus = "Skipped";
    private const int Maxheartbeats = 10;

    private const string PublisherRole = "Publisher";
    private const string SubscriptionsContainer = "subscriptions";
    private const string MessagesContainer = "messages";
    private const string AuditsContainer = "audits";

    //Has to be atleast 1 higher than rows showed in table
    private const int MaxSearchItemsCount = 100;

    public CosmosDbClient(CosmosClient cosmosClient, ILogger logger = null)
    {
        _cosmosClient = new CosmosClientAdapter(cosmosClient);
        _logger = logger;
    }

    public CosmosDbClient(ICosmosClientAdapter cosmosClient, ILogger logger = null)
    {
        _cosmosClient = cosmosClient;
        _logger = logger;
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
            EventTime = DateTime.Now,
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
                .ToFeedIterator();

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
            _logger?.Information($"COSMOS PAGING: Endpoint container not found for '{endpointId}'");
            return null;
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"COSMOS PAGING-ERROR: Failed to download endpoint state for '{endpointId}'");
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
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id));
            _logger?.Verbose(
                $"COSMOS UPSERT-RESPONSE: EventId: {eventId}, SessionId: {sessionId}, HttpStatusCode: {response.StatusCode}, Status : {CompletedStatus}");
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.Error(e,
                $"COSMOS UPSERT-ERROR: EventId: {eventId}, SessionId: {sessionId}, Status : {CompletedStatus}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

            throw;
        }
    }

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";

        try
        {
            var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.id = @id")
                .WithParameter("@id", id);
            var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

            if (result.HasMoreResults)
            {
                var eventDbo = await result.ReadNextAsync();
                if (eventDbo.Any())
                {
                    var updateEvent = eventDbo.First();
                    updateEvent.Deleted = true;
                    updateEvent.TimeToLive = 60; // 1 Minute
                    var response = await container.UpsertItemAsync<EventDbo>(updateEvent, new PartitionKey(updateEvent.Id));
                    _logger?.Verbose(
                        $"COSMOS REMOVE-MESSAGE: EventId: {eventId}, SessionId: {sessionId}, HttpStatusCode: {response.StatusCode}");
                    return true;
                }
            }

            return false;
        }
        catch (CosmosException e)
        {
            _logger?.Error(e,
                $"COSMOS REMOVE-MESSAGE: EventId: {eventId}, SessionId: {sessionId}, HttpStatusCode: {e.StatusCode}");

            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

            throw;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        try
        {
            var container = await GetEndpointContainer(endpointId);
            var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.sessionId = @sessionId")
                .WithParameter("@sessionId", sessionId);
            var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

            _logger?.Information(
                $"COSMOS PURGE: Deleted all messages on endpoint {endpointId} in session {sessionId}");
            while (result.HasMoreResults)
            {
                var eventDbo = await result.ReadNextAsync();
                if (eventDbo.Any())
                {
                    foreach (var queryResult in eventDbo)
                        await container.DeleteItemAsync<EventDbo>(queryResult.Id, new PartitionKey(queryResult.Id));
                }
            }

            return true;
        }
        catch (Exception e)
        {
            _logger?.Error(e,
                $"COSMOS PURGE: Couldn't delete all messages on endpoint {endpointId} in session {sessionId}");
            return false;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        try
        {
            var container = await GetEndpointContainer(endpointId);

            await container.DeleteContainerAsync();
            _logger?.Information($"COSMOS PURGE: Deleted all messages on endpoint {endpointId}");

            return true;
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"COSMOS PURGE: Couldn't delete all messages on endpoint {endpointId}");
            return false;
        }
    }

    public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, PendingStatus);

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
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
            {
                return eventDbo.First().Event;
            }
        }

        return null;
    }


    private async Task<UnresolvedEvent> GetEvent(string endpointId, string eventId, string sessionId, string status)
    {
        var container = await GetEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.id = @id AND c.status = @status AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@id", id)
            .WithParameter("@status", status);
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
            {
                return eventDbo.First().Event;
            }
        }

        return null;
    }

    public async Task<UnresolvedEvent> GetEventById(string endpointId, string id)
    {
        var container = await GetEndpointContainer(endpointId);
        try
        {
            var rel = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id), new ItemRequestOptions() { });
            return rel.Resource?.Event;
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
                    results.AddRange(response.Resource.Select(e => e.Event));
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
            { MaxItemCount = maxSearchItemsCount != 0 ? maxSearchItemsCount : MaxSearchItemsCount };
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


        if (filter.EndPointId != null)
            query = query
                .Where(x => x.Event.EndpointId.Contains(filter.EndPointId));

        if (filter.EventId != null)
            query = query
                .Where(x => x.Id.Contains(filter.EventId));

        if (filter.SessionId != null)
            query = query
                .Where(x => x.SessionId.Contains(filter.SessionId));

        if (filter.To != null)
            query = query
                .Where(x => x.Event.To.Contains(filter.To));

        if (filter.From != null)
            query = query
                .Where(x => x.Event.From.Contains(filter.From));

        if (filter.ResolutionStatus != null && filter.ResolutionStatus.Any())
            query = query
                .Where(x => filter.ResolutionStatus.Contains(x.Status));

        if (filter.MessageType != null)
            query = query
                .Where(x => x.Event.MessageType.ToString().Contains(filter.MessageType.ToString()));

        if (filter.Payload != null)
            query = query
                .Where(x => x.Event.MessageContent.EventContent.EventJson.Contains(filter.Payload));

        var result = query.OrderByDescending(e => e.Event.UpdatedAt).ToFeedIterator();
        var events = new List<UnresolvedEvent>();
        var token = "";
        var effectiveLimit = maxSearchItemsCount > 0 ? maxSearchItemsCount : MaxSearchItemsCount;
        while (result.HasMoreResults && events.Count <= effectiveLimit)
        {
            var eventDbo = await result.ReadNextAsync();
            token = eventDbo.ContinuationToken;
            foreach (var queryResult in eventDbo)
            {
                events.Add(queryResult.Event);
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
        var sqlQuery = $"SELECT * FROM c WHERE c.status ='{CompletedStatus}'";
        var result = container.GetItemQueryIterator<EventDbo>(sqlQuery, null, new QueryRequestOptions { });
        var unresolvedEvents = new List<UnresolvedEvent>();

        while (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            foreach (var queryResult in eventDbo)
            {
                unresolvedEvents.Add(queryResult.Event);
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

        var pageQuery = new QueryDefinition(
            "SELECT * FROM c WHERE c.sessionId = @sessionId AND c.status IN (@pendingStatus, @deferredStatus) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true) ORDER BY c.event.UpdatedAt DESC OFFSET @skip LIMIT @take")
            .WithParameter("@sessionId", sessionId)
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@skip", safeSkip)
            .WithParameter("@take", safeTake);
        var pageIterator = container.GetItemQueryIterator<EventDbo>(pageQuery);
        var items = new List<BlockedMessageEvent>();
        while (pageIterator.HasMoreResults)
        {
            var eventDbo = await pageIterator.ReadNextAsync();
            foreach (var queryResult in eventDbo)
            {
                items.Add(new BlockedMessageEvent
                {
                    EventId = queryResult.Event.EventId,
                    OriginatingId =
                        queryResult.Event.OriginatingMessageId.Equals("self", StringComparison.OrdinalIgnoreCase)
                            ? queryResult.Event.LastMessageId
                            : queryResult.Event.OriginatingMessageId,
                    Status = queryResult.Status
                });
            }
        }

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

        return new BlockedMessageEventPage
        {
            Items = items,
            Total = total,
        };
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var blockedMessageEvents = new List<UnresolvedEvent>();
        try
        {
            FeedIterator<EventDbo> queryResult = container.GetItemLinqQueryable<EventDbo>(true, null)
                .Where(e => e.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Deleted.HasValue || !e.Deleted.Value)
                .OrderByDescending(e => e.Event.UpdatedAt).ToFeedIterator();
            while (queryResult.HasMoreResults)
            {
                var eventDbo = await queryResult.ReadNextAsync();
                foreach (var pendingEvent in eventDbo)
                {
                    blockedMessageEvents.Add(pendingEvent.Event);
                }
            }
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.Information($"COSMOS PENDING-EVENTS: Endpoint container not found for '{endpointId}'");
            return null;
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"COSMOS PENDING-EVENTS-ERROR: Failed to get pending events for endpoint '{endpointId}'");
            throw;
        }

        return blockedMessageEvents;
    }

    public async Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var sqlQuery =
            $"SELECT * FROM c WHERE c.event.EndpointRole = '{PublisherRole}' AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)";
        var result = container.GetItemQueryIterator<EventDbo>(sqlQuery);
        var invalidMessageEvents = new List<BlockedMessageEvent>();

        while (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            foreach (var queryResult in eventDbo)
            {
                invalidMessageEvents.Add(new BlockedMessageEvent
                {
                    EventId = queryResult.Event.EventId,
                    OriginatingId =
                        queryResult.Event.OriginatingMessageId.Equals("self", StringComparison.OrdinalIgnoreCase)
                            ? queryResult.Event.LastMessageId
                            : queryResult.Event.OriginatingMessageId,
                    Status = queryResult.Status
                });
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
            _logger?.Verbose(
                $"COSMOS SUBSCRIPTION: endpointId: {subscription.EndpointId}, SubscriptionId: {subscription.Id}, HttpStatusCode: {response.StatusCode}");
            return subscription;
        }

        _logger?.Error(
            $"COSMOS SUBSCRIPTION ERROR: endpointId: {subscription.EndpointId}, SubscriptionId: {subscription.Id}, HttpStatusCode: {response.StatusCode}");
        return null; //Return error?
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
    {
        var subscriptions = new List<EndpointSubscription>();
        var db = _cosmosClient.GetDatabase(DatabaseId);
        var subscriptionContainer = await db.CreateContainerIfNotExistsAsync(SubscriptionsContainer, "/id");

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
        var db = _cosmosClient.GetDatabase(DatabaseId);
        var subscriptionContainer = await db.CreateContainerIfNotExistsAsync(SubscriptionsContainer, "/id");

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
            _logger?.Verbose(
                $"COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {subscriptionId}, HttpStatusCode: {response.StatusCode}");
            return true;
        }
        catch (Exception e)
        {
            _logger?.Error(
                $"COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {subscriptionId}, Exception: {e.Message}");
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
            _logger?.Verbose(
                $"COSMOS REMOVE-SUBSCRIPTION: endpointId: {endpointId}, SubscriptionId: {id}, HttpStatusCode: {response.StatusCode}");
            return true;
        }
        catch (Exception e)
        {
            _logger?.Error(
                $"COSMOS REMOVE-SUBSCRIPTION: endpointId: {endpointId}, SubscriptionId: {id}, Exception: {e.Message}");
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
            _logger?.Error(e,
                $"COSMOS UPDATE-SUBSCRIPTION: Endpoint: {subscription.EndpointId}, SubscriptionId: {subscription.Id}, Exception: {e.Message}");
            return false;
        }
    }

    public async Task<string> GetEndpointErrorList(string endpointId)
    {
        var container = await GetEndpointContainer(endpointId);
        var sqlQuery =
            $"SELECT * FROM c WHERE c.status IN ('{FailedStatus}', '{DeferredStatus}') AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)";

        var result = container.GetItemQueryIterator<EventDbo>(sqlQuery);
        var failedAndDefferedlist = "";
        while (result.HasMoreResults)
        {
            var message = await result.ReadNextAsync();
            foreach (var queryResult in message)
            {
                failedAndDefferedlist += $"{queryResult.Id};";
            }
        }

        return failedAndDefferedlist;
    }

    private async Task<ICosmosContainerAdapter> GetEndpointContainer(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
        {
            throw new ArgumentNullException(nameof(endpointId), "EndpointId cannot be null or empty");
        }
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await db.CreateContainerIfNotExistsAsync(endpointId, "/id");
    }

    private async Task<ICosmosContainerAdapter> GetMessagesContainer()
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await db.CreateContainerIfNotExistsAsync(MessagesContainer, "/eventId");
    }

    private async Task<ICosmosContainerAdapter> GetAuditsContainer()
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);
        return await db.CreateContainerIfNotExistsAsync(AuditsContainer, "/eventId");
    }

    public async Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        var container = await GetMessagesContainer();
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();
        var paramIndex = 0;

        string NextParam() => $"@p{paramIndex++}";

        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(LOWER(c.endpointId), {p})");
            parameters[p] = filter.EndpointId.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(filter.EventId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(c.eventId, {p})");
            parameters[p] = filter.EventId;
        }

        if (!string.IsNullOrEmpty(filter.MessageId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(c.id, {p})");
            parameters[p] = filter.MessageId;
        }

        if (!string.IsNullOrEmpty(filter.SessionId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(c.message.SessionId, {p})");
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

        var sql = "SELECT * FROM c";
        if (conditions.Any())
            sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY c.message.EnqueuedTimeUtc DESC";

        var queryDef = new QueryDefinition(sql);
        foreach (var kvp in parameters)
            queryDef = queryDef.WithParameter(kvp.Key, kvp.Value);

        var requestOptions = new QueryRequestOptions { MaxItemCount = maxItemCount > 0 ? maxItemCount : 50 };
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
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId));
        }
        catch (CosmosException e)
        {
            _logger?.Error(e, $"COSMOS STORE-MESSAGE-ERROR: EventId: {message.EventId}, MessageId: {message.MessageId}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

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
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId));
        }
        catch (CosmosException e)
        {
            _logger?.Error(e, $"COSMOS STORE-AUDIT-ERROR: EventId: {eventId}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

            throw;
        }
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        var container = await GetAuditsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId ORDER BY c.createdAt DESC")
            .WithParameter("@eventId", eventId);
        var result = container.GetItemQueryIterator<AuditDocument>(query);
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

        if (!string.IsNullOrEmpty(filter.EventId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(c.eventId, {p})");
            parameters[p] = filter.EventId;
        }

        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(LOWER(c.endpointId), {p})");
            parameters[p] = filter.EndpointId.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(filter.AuditorName))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(LOWER(c.audit.auditorName), {p})");
            parameters[p] = filter.AuditorName.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(filter.EventTypeId))
        {
            var p = NextParam();
            conditions.Add($"CONTAINS(LOWER(c.eventTypeId), {p})");
            parameters[p] = filter.EventTypeId.ToLowerInvariant();
        }

        if (filter.AuditType != null)
        {
            var p = NextParam();
            conditions.Add($"c.audit.auditType = {p}");
            parameters[p] = filter.AuditType.Value.ToString();
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

        var requestOptions = new QueryRequestOptions { MaxItemCount = maxItemCount > 0 ? maxItemCount : 50 };
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
            _logger?.Verbose($"COSMOS ARCHIVE-FAILED: Event not found. EventId: {eventId}, SessionId: {sessionId}, EndpointId: {endpointId}");
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
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id));
            _logger?.Verbose(
                $"COSMOS UPSERT-RESPONSE: EventId: {eventId}, SessionId: {sessionId}, HttpStatusCode: {response.StatusCode}, Status : {status}");
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.Error(e,
                $"COSMOS UPSERT-ERROR: EventId: {eventId}, SessionId: {sessionId}, Status : {status}, HttpStatusCode: {e.StatusCode}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

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
            _logger?.Information($"COSMOS METADATAS: Some endpoints not found in metadata container");
            return null;
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"COSMOS METADATAS-ERROR: Failed to get metadatas for endpoints");
            throw;
        }
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        var sqlQuery = $"SELECT * FROM c";
        var metadatas = await GetMetadatasByFilter(sqlQuery);

        return metadatas;
    }

    public async Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
    {
        var sqlQuery = $"SELECT * FROM c WHERE c.IsHeartbeatEnabled = true";
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

    public async Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        var container = await GetEndpointContainer("Metadata");
        List<PatchOperation> patchOperations = new List<PatchOperation>()
        {
            PatchOperation.Replace("/IsHeartbeatEnabled", enable),
        };

        try
        {
            await container.PatchItemAsync<EndpointMetadata>(endpointId, new PartitionKey(endpointId), patchOperations);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await SetEndpointMetadata(new EndpointMetadata
            {
                EndpointId = endpointId,
                IsHeartbeatEnabled = enable,
                Heartbeats = new List<Heartbeat>(),
                TechnicalContacts = new List<TechnicalContact>(),
            });
        }
    }

    public async Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var container = await GetEndpointContainer("Metadata");

        try
        {
            var response =
                await container.UpsertItemAsync(endpointMetadata, new PartitionKey(endpointMetadata.EndpointId));
            _logger?.Verbose(
                $"COSMOS UPSERT-RESPONSE: Metadata upsert. Id: {endpointMetadata.EndpointId}, HttpStatusCode: {response.StatusCode}");
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.Error(e,
                $"COSMOS UPSERT-ERROR: Metadata upsert. Id: {endpointMetadata.EndpointId}, HttpStatusCode: {e.StatusCode}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

            throw;
        }
    }

    public async Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        var container = await GetEndpointContainer("Metadata");

        try
        {
            var metadata = await GetEndpointMetadata(endpointId);
            metadata.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;

            // Check if heartbeat exists
            if (metadata.Heartbeats == null)
                metadata.Heartbeats = new List<Heartbeat>();
            // Check if id exists
            var existingHeartbeat = metadata.Heartbeats.FirstOrDefault(h => h.MessageId == heartbeat.MessageId);
            if (existingHeartbeat != null)
            {
                existingHeartbeat.ReceivedTime = heartbeat.ReceivedTime;
                existingHeartbeat.EndTime = heartbeat.EndTime;
                existingHeartbeat.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;
            }
            else
            {
                if (metadata.Heartbeats.Count >= Maxheartbeats)
                {
                    metadata.Heartbeats = metadata.Heartbeats.OrderBy(h => h.StartTime).Skip(1).ToList();
                    metadata.Heartbeats.Add(heartbeat);
                }
                else
                {
                    metadata.Heartbeats.Add(heartbeat);
                }
            }

            var response =
                await container.UpsertItemAsync(metadata, new PartitionKey(endpointId));
            _logger?.Verbose(
                $"COSMOS UPSERT-RESPONSE: Metadata upsert. Id: {endpointId}, HttpStatusCode: {response.StatusCode}");
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.Error(e,
                $"COSMOS UPSERT-ERROR: Metadata upsert. Id: {endpointId}, HttpStatusCode: {e.StatusCode}");

            if (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RequestLimitException(e.RetryAfter);
            }

            throw;
        }
    }

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
    {
        var container = await GetMessagesContainer();
        var fromIso = from.ToString("o");

        var published = await RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.message[\"From\"] AS endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.message[\"From\"], c.message.EventTypeId",
            fromIso);

        var failed = await RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        var handled = await RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso);

        return new EndpointMetricsResult
        {
            Published = published,
            Handled = handled,
            Failed = failed
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

        var queueRows = await RunLatencyAggregateQuery(container,
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

        var processingRows = await RunLatencyAggregateQuery(container,
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
        var iterator = container.GetItemQueryIterator<FailedMessageQueryResult>(query);
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

        var publishedBuckets = await RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

        var handledBuckets = await RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

        var failedBuckets = await RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso);

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
