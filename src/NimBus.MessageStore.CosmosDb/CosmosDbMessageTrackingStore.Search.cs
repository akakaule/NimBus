using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System.Net;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    private static BlockedMessageEvent ToBlockedMessageEvent(BlockedEventProjection projection) => new()
    {
        Status = projection.Status,
        EventId = projection.EventId,
        OriginatingId = BlockedEventRules.ResolveOriginatingId(projection.OriginatingMessageId, projection.LastMessageId)
    };

    public async Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken,
        int maxSearchItemsCount)
    {
        var container = await _getEndpointContainer(filter.EndPointId);
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

    public async Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId,
        string sessionId, int skip, int take)
    {
        var safeSkip = skip < 0 ? 0 : skip;
        var safeTake = PaginationLimits.Resolve(take);

        var container = await _getEndpointContainer(endpointId);

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
        var container = await _getEndpointContainer(endpointId);
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
        var container = await _getEndpointContainer(endpointId);
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

    public async Task<string> GetEndpointErrorList(string endpointId)
    {
        var container = await _getEndpointContainer(endpointId);
        // Only the id is used by the caller — project it server-side instead of
        // reading whole EventDbo documents (large EventJson / stack traces), which
        // cut RU and payload by 10-50x. Accumulate then join once (no O(n^2) concat).
        var queryDefinition = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.status IN (@failedStatus, @deferredStatus) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@failedStatus", EndpointErrorListFormat.FailedStatus)
            .WithParameter("@deferredStatus", EndpointErrorListFormat.DeferredStatus);

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

        return EndpointErrorListFormat.Format(ids);
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
        var container = await _getMessagesContainer();
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
}
