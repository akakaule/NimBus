using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Pure in-memory implementation of <see cref="INimBusMessageStore"/>. Intended for
/// the conformance suite (where it serves as the always-green reference) and for
/// unit tests that need a working store without provisioning Cosmos or SQL Server.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
///
/// Most methods are minimum-viable: enough surface to exercise the resolver state
/// transitions, message persistence, and basic queries the conformance suite cares
/// about. Search/pagination methods return empty results.
/// </summary>
public class InMemoryMessageStore : INimBusMessageStore
{
    private readonly ConcurrentDictionary<(string Endpoint, string EventId, string Session), UnresolvedEvent> _events = new();
    private readonly ConcurrentDictionary<(string EventId, string MessageId), MessageEntity> _messages = new();
    private readonly ConcurrentDictionary<string, List<MessageAuditEntity>> _audits = new();
    private readonly ConcurrentDictionary<string, EndpointSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, EndpointMetadata> _metadata = new();
    private readonly List<Heartbeat> _heartbeats = new();
    private readonly object _heartbeatLock = new();

    private (string, string, string) Key(string endpoint, string eventId, string session) => (endpoint, eventId, session ?? string.Empty);

    private Task<bool> Upsert(string eventId, string sessionId, string endpointId, ResolutionStatus status, UnresolvedEvent content)
    {
        content.ResolutionStatus = status;
        content.UpdatedAt = DateTime.UtcNow;
        content.EndpointId = endpointId;
        content.EventId = eventId;
        content.SessionId = sessionId;
        _events[Key(endpointId, eventId, sessionId)] = content;
        return Task.FromResult(true);
    }

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Pending, content);
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Deferred, content);
    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Failed, content);
    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.DeadLettered, content);
    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Unsupported, content);
    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Skipped, content);
    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Completed, content);

    public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Pending);
    public Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Failed);
    public Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Deferred);
    public Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.DeadLettered);
    public Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Unsupported);

    private Task<UnresolvedEvent> GetByStatus(string endpointId, string eventId, string sessionId, ResolutionStatus status)
    {
        var match = _events.Values.FirstOrDefault(e => e.EndpointId == endpointId && e.EventId == eventId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty) && e.ResolutionStatus == status);
        return match == null ? throw new EndpointNotFoundException(endpointId) : Task.FromResult(match);
    }

    public Task<UnresolvedEvent> GetEvent(string endpointId, string eventId)
    {
        var match = _events.Values.Where(e => e.EndpointId == endpointId && e.EventId == eventId).OrderByDescending(e => e.UpdatedAt).FirstOrDefault();
        return match == null ? throw new EndpointNotFoundException(endpointId) : Task.FromResult(match);
    }

    public Task<UnresolvedEvent> GetEventById(string endpointId, string id) => GetEvent(endpointId, id);

    public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var set = new HashSet<string>(eventIds);
        return Task.FromResult(_events.Values.Where(e => e.EndpointId == endpointId && set.Contains(e.EventId)).ToList());
    }

    public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
        => Task.FromResult<IEnumerable<UnresolvedEvent>>(_events.Values.Where(e => e.EndpointId == endpointId && e.ResolutionStatus == ResolutionStatus.Completed).ToList());

    public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount)
    {
        IEnumerable<UnresolvedEvent> q = _events.Values;
        if (!string.IsNullOrEmpty(filter.EndPointId)) q = q.Where(e => e.EndpointId == filter.EndPointId);
        if (!string.IsNullOrEmpty(filter.EventId)) q = q.Where(e => e.EventId == filter.EventId);
        if (!string.IsNullOrEmpty(filter.SessionId)) q = q.Where(e => e.SessionId == filter.SessionId);
        if (filter.ResolutionStatus is { Count: > 0 })
        {
            var statuses = new HashSet<string>(filter.ResolutionStatus);
            q = q.Where(e => statuses.Contains(e.ResolutionStatus.ToString()));
        }
        var results = q.OrderByDescending(e => e.UpdatedAt).Take(maxSearchItemsCount > 0 ? maxSearchItemsCount : 100).ToList();
        return Task.FromResult(new SearchResponse { Events = results });
    }

    public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var grouped = _events.Values.Where(e => e.EndpointId == endpointId).GroupBy(e => e.ResolutionStatus).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            PendingCount = grouped.GetValueOrDefault(ResolutionStatus.Pending),
            DeferredCount = grouped.GetValueOrDefault(ResolutionStatus.Deferred),
            FailedCount = grouped.GetValueOrDefault(ResolutionStatus.Failed),
            DeadletterCount = grouped.GetValueOrDefault(ResolutionStatus.DeadLettered),
            UnsupportedCount = grouped.GetValueOrDefault(ResolutionStatus.Unsupported),
        });
    }

    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
        => Task.FromResult(new SessionStateCount { SessionId = sessionId, PendingEvents = Array.Empty<string>(), DeferredEvents = Array.Empty<string>() });

    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
        => Task.FromResult<IEnumerable<SessionStateCount>>(sessionIds.Select(s => new SessionStateCount { SessionId = s, PendingEvents = Array.Empty<string>(), DeferredEvents = Array.Empty<string>() }).ToList());

    public Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
        => Task.FromResult(new EndpointState { EndpointId = endpointId, EventTime = DateTime.UtcNow });

    public Task<IEnumerable<BlockedMessageEvent>> GetBlockedEventsOnSession(string endpointId, string sessionId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(Array.Empty<BlockedMessageEvent>());

    public Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<UnresolvedEvent>>(_events.Values.Where(e => e.EndpointId == endpointId && e.ResolutionStatus == ResolutionStatus.Pending).ToList());

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(Array.Empty<BlockedMessageEvent>());

    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
        => Task.FromResult(_events.TryRemove(Key(endpointId, eventId, sessionId), out _));

    public Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        var keys = _events.Keys.Where(k => k.Item1 == endpointId && k.Item3 == (sessionId ?? string.Empty)).ToList();
        foreach (var k in keys) _events.TryRemove(k, out _);
        return Task.FromResult(keys.Count > 0);
    }

    public Task<bool> PurgeMessages(string endpointId)
    {
        var keys = _events.Keys.Where(k => k.Item1 == endpointId).ToList();
        foreach (var k in keys) _events.TryRemove(k, out _);
        return Task.FromResult(keys.Count > 0);
    }

    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        _events.TryRemove(Key(endpointId, eventId, sessionId), out _);
        return Task.CompletedTask;
    }

    public Task StoreMessage(MessageEntity message)
    {
        _messages[(message.EventId, message.MessageId)] = message;
        return Task.CompletedTask;
    }

    public Task<MessageEntity> GetMessage(string eventId, string messageId)
        => _messages.TryGetValue((eventId, messageId), out var m)
            ? Task.FromResult(m)
            : throw new MessageNotFoundException(eventId, messageId);

    public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
        => Task.FromResult<IEnumerable<MessageEntity>>(_messages.Values.Where(m => m.EventId == eventId).ToList());

    public Task<MessageEntity> GetFailedMessage(string eventId, string endpointId)
    {
        var match = _messages.Values.FirstOrDefault(m => m.EventId == eventId && m.EndpointId == endpointId);
        return match == null ? throw new MessageNotFoundException(eventId) : Task.FromResult(match);
    }

    public Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId)
        => GetFailedMessage(eventId, endpointId);

    public Task RemoveStoredMessage(string eventId, string messageId)
    {
        _messages.TryRemove((eventId, messageId), out _);
        return Task.CompletedTask;
    }

    public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        IEnumerable<MessageEntity> q = _messages.Values;
        if (!string.IsNullOrEmpty(filter.EndpointId)) q = q.Where(m => m.EndpointId == filter.EndpointId);
        if (!string.IsNullOrEmpty(filter.EventId)) q = q.Where(m => m.EventId == filter.EventId);
        if (!string.IsNullOrEmpty(filter.MessageId)) q = q.Where(m => m.MessageId == filter.MessageId);
        if (!string.IsNullOrEmpty(filter.SessionId)) q = q.Where(m => m.SessionId == filter.SessionId);
        if (!string.IsNullOrEmpty(filter.From)) q = q.Where(m => m.From == filter.From);
        if (!string.IsNullOrEmpty(filter.To)) q = q.Where(m => m.To == filter.To);
        var results = q.OrderByDescending(m => m.EnqueuedTimeUtc).Take(maxItemCount > 0 ? maxItemCount : 100).ToList();
        return Task.FromResult(new MessageSearchResult { Messages = results });
    }

    public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        _audits.GetOrAdd(eventId, _ => new List<MessageAuditEntity>()).Add(auditEntity);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
        => Task.FromResult<IEnumerable<MessageAuditEntity>>(_audits.GetValueOrDefault(eventId) ?? new List<MessageAuditEntity>());

    public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var allAudits = _audits.SelectMany(kvp => kvp.Value.Select(a => new AuditSearchItem
        {
            EventId = kvp.Key,
            Audit = a,
            CreatedAt = a.AuditTimestamp,
        }));
        if (!string.IsNullOrEmpty(filter.EventId)) allAudits = allAudits.Where(a => a.EventId == filter.EventId);
        if (!string.IsNullOrEmpty(filter.AuditorName)) allAudits = allAudits.Where(a => a.Audit.AuditorName == filter.AuditorName);
        if (filter.AuditType.HasValue) allAudits = allAudits.Where(a => a.Audit.AuditType == filter.AuditType.Value);
        var results = allAudits.OrderByDescending(a => a.CreatedAt).Take(maxItemCount > 0 ? maxItemCount : 100).ToList();
        return Task.FromResult(new AuditSearchResult { Audits = results });
    }

    public Task<string> GetEndpointErrorList(string endpointId) => Task.FromResult(string.Empty);

    public Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var sub = new EndpointSubscription
        {
            Id = Guid.NewGuid().ToString(),
            EndpointId = endpointId, Mail = mail, Type = type, AuthorId = author, Url = url,
            EventTypes = eventTypes, Payload = payload, Frequency = frequency,
        };
        _subscriptions[sub.Id] = sub;
        return Task.FromResult(sub);
    }

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
        => Task.FromResult<IEnumerable<EndpointSubscription>>(_subscriptions.Values.Where(s => s.EndpointId == endpointId).ToList());

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText)
        => GetSubscriptionsOnEndpoint(endpoint);

    public Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        if (!_subscriptions.ContainsKey(subscription.Id)) return Task.FromResult(false);
        _subscriptions[subscription.Id] = subscription;
        return Task.FromResult(true);
    }

    public Task<bool> UnsubscribeById(string endpointId, string id)
        => Task.FromResult(_subscriptions.TryRemove(id, out _));

    public Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        var match = _subscriptions.Values.FirstOrDefault(s => s.EndpointId == endpointId && s.Mail == mail);
        return match == null ? Task.FromResult(false) : Task.FromResult(_subscriptions.TryRemove(match.Id, out _));
    }

    public Task<bool> DeleteSubscription(string subscriptionId)
        => Task.FromResult(_subscriptions.TryRemove(subscriptionId, out _));

    public Task<EndpointMetadata> GetEndpointMetadata(string endpointId)
        => _metadata.TryGetValue(endpointId, out var m) ? Task.FromResult(m) : throw new EndpointNotFoundException(endpointId);

    public Task<List<EndpointMetadata>> GetMetadatas() => Task.FromResult(_metadata.Values.ToList());

    public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var set = new HashSet<string>(endpointIds);
        return Task.FromResult<List<EndpointMetadata>?>(_metadata.Values.Where(m => set.Contains(m.EndpointId)).ToList());
    }

    public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
        => Task.FromResult(_metadata.Values.Where(m => m.IsHeartbeatEnabled == true).ToList());

    public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        _metadata[endpointMetadata.EndpointId] = endpointMetadata;
        return Task.FromResult(true);
    }

    public Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        if (!_metadata.TryGetValue(endpointId, out var meta))
            meta = _metadata[endpointId] = new EndpointMetadata { EndpointId = endpointId };
        meta.IsHeartbeatEnabled = enable;
        return Task.CompletedTask;
    }

    public Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        lock (_heartbeatLock) _heartbeats.Add(heartbeat);
        return Task.FromResult(true);
    }

    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => Task.FromResult(new EndpointMetricsResult());
    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => Task.FromResult(new EndpointLatencyMetricsResult());
    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => Task.FromResult(new List<FailedMessageInfo>());
    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => Task.FromResult(new TimeSeriesResult { BucketSize = bucketLabel });
}
