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
        return Task.FromResult(_events.Values.Where(e => e.EndpointId == endpointId && (set.Contains(e.EventId) || set.Contains(CompositeEventId(e)))).ToList());
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
    {
        var events = _events.Values
            .Where(e => e.EndpointId == endpointId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty))
            .ToList();

        return Task.FromResult(new SessionStateCount
        {
            SessionId = sessionId,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
        });
    }

    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
        => Task.FromResult<IEnumerable<SessionStateCount>>(sessionIds.Select(s => DownloadEndpointSessionStateCount(endpointId, s).GetAwaiter().GetResult()).ToList());

    public Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
    {
        var take = pageSize > 0 ? pageSize : 100;
        var events = _events.Values
            .Where(e => e.EndpointId == endpointId)
            .Where(e => e.ResolutionStatus is ResolutionStatus.Pending or ResolutionStatus.Deferred or ResolutionStatus.Failed or ResolutionStatus.DeadLettered or ResolutionStatus.Unsupported)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(take)
            .ToList();

        return Task.FromResult(new EndpointState
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            EnrichedUnresolvedEvents = events,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
            FailedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Failed).Select(CompositeEventId).ToList(),
            DeadletteredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.DeadLettered).Select(CompositeEventId).ToList(),
            UnsupportedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Unsupported).Select(CompositeEventId).ToList(),
            ContinuationToken = events.Count == take ? "more" : string.Empty,
        });
    }

    public Task<IEnumerable<BlockedMessageEvent>> GetBlockedEventsOnSession(string endpointId, string sessionId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(_events.Values
            .Where(e => e.EndpointId == endpointId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty))
            .Where(e => e.ResolutionStatus is ResolutionStatus.Pending or ResolutionStatus.Deferred)
            .Select(ToBlockedMessageEvent)
            .ToList());

    public Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<UnresolvedEvent>>(_events.Values.Where(e => e.EndpointId == endpointId && e.ResolutionStatus == ResolutionStatus.Pending).ToList());

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(_events.Values
            .Where(e => e.EndpointId == endpointId && e.EndpointRole == EndpointRole.Publisher)
            .Select(ToBlockedMessageEvent)
            .ToList());

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

    public Task<string> GetEndpointErrorList(string endpointId)
    {
        var ids = _events.Values
            .Where(e => e.EndpointId == endpointId)
            .Where(e => e.ResolutionStatus is ResolutionStatus.Failed or ResolutionStatus.Deferred)
            .Select(CompositeEventId)
            .ToList();

        return Task.FromResult(ids.Count == 0 ? string.Empty : string.Join(";", ids) + ";");
    }

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
        endpointMetadata.TechnicalContacts ??= new List<TechnicalContact>();
        endpointMetadata.Heartbeats ??= new List<Heartbeat>();
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
        lock (_heartbeatLock)
        {
            _heartbeats.Add(heartbeat);
            if (!_metadata.TryGetValue(endpointId, out var metadata))
            {
                metadata = new EndpointMetadata { EndpointId = endpointId };
                _metadata[endpointId] = metadata;
            }

            metadata.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;
            metadata.Heartbeats ??= new List<Heartbeat>();
            var existing = metadata.Heartbeats.FirstOrDefault(h => h.MessageId == heartbeat.MessageId);
            if (existing == null)
            {
                metadata.Heartbeats.Add(heartbeat);
            }
            else
            {
                existing.StartTime = heartbeat.StartTime;
                existing.ReceivedTime = heartbeat.ReceivedTime;
                existing.EndTime = heartbeat.EndTime;
                existing.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;
            }
        }
        return Task.FromResult(true);
    }

    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
    {
        var messages = _messages.Values.Where(m => m.EnqueuedTimeUtc >= from).ToList();
        return Task.FromResult(new EndpointMetricsResult
        {
            Published = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.EventRequest), published: true),
            Handled = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.ResolutionResponse), published: false),
            Failed = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.ErrorResponse), published: false),
        });
    }

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from)
    {
        var outcomeTypes = new HashSet<MessageType>
        {
            MessageType.ResolutionResponse,
            MessageType.ErrorResponse,
            MessageType.SkipResponse,
            MessageType.DeferralResponse,
            MessageType.UnsupportedResponse,
        };

        var latencies = _messages.Values
            .Where(m => m.EnqueuedTimeUtc >= from && outcomeTypes.Contains(m.MessageType))
            .GroupBy(m => (EndpointId: m.EndpointId ?? string.Empty, EventTypeId: m.EventTypeId ?? string.Empty))
            .Select(g => new EndpointLatencyAggregate
            {
                EndpointId = g.Key.EndpointId,
                EventTypeId = g.Key.EventTypeId,
                Queue = Aggregate(g.Select(m => m.QueueTimeMs)),
                Processing = Aggregate(g.Select(m => m.ProcessingTimeMs)),
            })
            .ToList();

        return Task.FromResult(new EndpointLatencyMetricsResult { Latencies = latencies });
    }

    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from)
    {
        var results = _messages.Values
            .Where(m => m.EnqueuedTimeUtc >= from && m.MessageType == MessageType.ErrorResponse)
            .Select(m => new FailedMessageInfo
            {
                EndpointId = m.EndpointId,
                EventTypeId = m.EventTypeId,
                ErrorText = m.MessageContent?.ErrorContent?.ErrorText ?? m.DeadLetterErrorDescription ?? string.Empty,
                EnqueuedTimeUtc = m.EnqueuedTimeUtc,
                EventId = m.EventId,
            })
            .ToList();

        return Task.FromResult(results);
    }

    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
    {
        var buckets = GenerateBucketKeys(from, DateTime.UtcNow, substringLength)
            .ToDictionary(k => k, k => new TimeSeriesBucket { Timestamp = k });

        foreach (var message in _messages.Values.Where(m => m.EnqueuedTimeUtc >= from))
        {
            var key = message.EnqueuedTimeUtc.ToUniversalTime().ToString("o")[..substringLength];
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TimeSeriesBucket { Timestamp = key };
                buckets[key] = bucket;
            }

            switch (message.MessageType)
            {
                case MessageType.EventRequest:
                    bucket.Published++;
                    break;
                case MessageType.ResolutionResponse:
                    bucket.Handled++;
                    break;
                case MessageType.ErrorResponse:
                    bucket.Failed++;
                    break;
            }
        }

        return Task.FromResult(new TimeSeriesResult
        {
            BucketSize = bucketLabel,
            DataPoints = buckets.Values.OrderBy(b => b.Timestamp).ToList(),
        });
    }

    private static List<EndpointEventTypeCount> CountByEndpointAndEventType(IEnumerable<MessageEntity> messages, bool published)
        => messages
            .GroupBy(m => (
                EndpointId: published ? (m.From ?? string.Empty) : (m.EndpointId ?? string.Empty),
                EventTypeId: m.EventTypeId ?? string.Empty))
            .Select(g => new EndpointEventTypeCount
            {
                EndpointId = g.Key.EndpointId,
                EventTypeId = g.Key.EventTypeId,
                Count = g.Count(),
            })
            .ToList();

    private static string CompositeEventId(UnresolvedEvent @event)
        => $"{@event.EventId}_{@event.SessionId ?? string.Empty}";

    private static BlockedMessageEvent ToBlockedMessageEvent(UnresolvedEvent @event)
    {
        var originatingMessageId = @event.OriginatingMessageId ?? string.Empty;
        return new BlockedMessageEvent
        {
            EventId = @event.EventId,
            OriginatingId = string.Equals(originatingMessageId, "self", StringComparison.OrdinalIgnoreCase)
                ? @event.LastMessageId
                : originatingMessageId,
            Status = @event.ResolutionStatus.ToString(),
        };
    }

    private static LatencyAggregate Aggregate(IEnumerable<long?> values)
    {
        var captured = values.Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        return captured.Count == 0
            ? new LatencyAggregate()
            : new LatencyAggregate
            {
                Count = captured.Count,
                AvgMs = captured.Average(),
                MinMs = captured.Min(),
                MaxMs = captured.Max(),
            };
    }

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
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

        var keys = new List<string>();
        while (current <= to)
        {
            keys.Add(current.ToString("o")[..substringLength]);
            current += step;
        }

        return keys;
    }
}
