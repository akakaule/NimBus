using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations covering the lifecycle of a tracked message: status transitions,
/// per-message records, audit trail, search, and bulk operations. Implemented by each
/// storage provider (Cosmos DB, SQL Server, in-memory test fake, etc.).
/// </summary>
public interface IMessageTrackingStore
{
    // Status transition uploads
    Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);
    Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    // Single-event lookups
    Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId);
    Task<UnresolvedEvent> GetEvent(string endpointId, string eventId);
    Task<UnresolvedEvent> GetEventById(string endpointId, string id);
    Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds);
    Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId);

    /// <summary>
    /// Locate the single pending-handoff row whose ExternalJobId matches, scoped
    /// to the endpoint that owns the handoff. Used by the operator-side tooling
    /// — the WebApp's "settle this stuck handoff" action and CLI diagnostics —
    /// that needs to resolve audit-row coordinates from the external correlation
    /// token an adapter persisted.
    ///
    /// <para>Not used by <c>IHandoffClient</c>: adapters carry the audit-row
    /// coordinates themselves (via <c>HandoffSettlement</c>) so the settlement
    /// process needs no audit-DB access. See ADR-012 for the rationale.</para>
    ///
    /// <para>Returns null when no matching pending-handoff row exists (already
    /// settled, never registered, or wrong job id). The endpoint scope keeps
    /// Cosmos partitioning correct and lets SQL Server hit a filtered index
    /// (see 0011_HandoffLookup.sql).</para>
    /// </summary>
    Task<UnresolvedEvent> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single oldest (or, for providers that cannot cheaply order, any) pending
    /// event on <paramref name="endpointId"/> whose <c>PendingSubStatus</c> is <c>"Handoff"</c>,
    /// optionally restricted to the given <paramref name="eventTypeIds"/>. Returns null when none.
    ///
    /// <para>Backs the agent receive long-poll, which previously materialised every pending event
    /// (full <c>EventJson</c>) and filtered client-side. All filters (status, sub-status, event
    /// type) are applied server-side and the query is bounded to a single result so the poll stays
    /// cheap on Cosmos.</para>
    /// </summary>
    /// <param name="endpointId">The endpoint (partition) that owns the handoff.</param>
    /// <param name="eventTypeIds">
    /// Restrict to these event types; null or empty matches any event type.
    /// </param>
    Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds);

    // Filtered queries

    /// <summary>
    /// Filtered event search. ID-like string filters (endpoint id, event id,
    /// session id) match by case-insensitive PREFIX on every provider; free-text
    /// filters (To/From/Payload) keep provider-specific substring semantics.
    /// Results omit <c>MessageContent.EventContent.EventJson</c> — detail views
    /// fetch the payload on demand via <see cref="GetLatestEventRequestMessage"/>;
    /// <c>ErrorContent</c> is kept (the error-grouped view reads it).
    /// </summary>
    Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount);

    // State counts
    Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId);
    Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId);
    Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds);
    Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken);

    // Session views
    Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take);
    Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId);
    Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId);

    // Lifecycle / cleanup
    Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId);
    Task<bool> PurgeMessages(string endpointId, string sessionId);
    Task<bool> PurgeMessages(string endpointId);
    Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId);

    // Per-message history records
    Task StoreMessage(MessageEntity message);
    Task<MessageEntity> GetMessage(string eventId, string messageId);
    Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId);
    /// <summary>
    /// Returns the most recent message that carries event content (an
    /// <c>EventRequest</c> or <c>ResubmissionRequest</c> with a non-empty
    /// <c>EventJson</c>), or <c>null</c> when none exists. Lets callers obtain the
    /// "current request payload" without materialising the whole event history.
    /// </summary>
    Task<MessageEntity> GetLatestEventRequestMessage(string eventId);
    Task<MessageEntity> GetFailedMessage(string eventId, string endpointId);
    Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId);
    Task RemoveStoredMessage(string eventId, string messageId);

    /// <summary>
    /// Filtered message search. ID-like string filters (endpoint id, event id,
    /// message id, session id) match by case-insensitive PREFIX on every
    /// provider. Results omit <c>MessageContent.EventContent.EventJson</c> —
    /// detail views fetch the payload via <see cref="GetMessage"/>;
    /// <c>ErrorContent</c> is kept.
    /// </summary>
    Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount);

    // Audit trail
    Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null);
    Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId);

    /// <summary>
    /// Filtered audit search. ID-like string filters (event id, endpoint id,
    /// auditor name, event type id) match by case-insensitive PREFIX on every
    /// provider — except that <see cref="AuditFilter.EndpointIdExact"/> switches
    /// the endpoint id to case-insensitive EQUALITY (used by
    /// authorization-scoped callers). Providers unaware of the flag fall back to
    /// prefix semantics; security-sensitive callers must therefore keep a final
    /// exact check on the results.
    /// </summary>
    Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount);

    /// <summary>
    /// Number of resubmissions per event, counted from the audit trail
    /// (<see cref="MessageAuditType.Resubmit"/> + <see cref="MessageAuditType.ResubmitWithChanges"/>),
    /// for the given events on <paramref name="endpointId"/>. Denied attempts
    /// (<see cref="MessageAuditEntity.AccessDenied"/>) are excluded — they never
    /// resubmitted. Events with no resubmit audits are absent from the result
    /// (treat missing as 0). Implementations answer with a single grouped query
    /// so callers can enrich a whole search page in one round trip.
    /// <para>Default implementation returns an empty map so pre-existing
    /// external providers keep compiling; the UI then simply shows 0
    /// resubmissions.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    // Event reports ("reported" markers)

    /// <summary>
    /// Upserts the per-event "reported" marker keyed on
    /// (<paramref name="endpointId"/>, <paramref name="eventId"/>).
    /// <see cref="EventReport.ReportedAtUtc"/> is stamped with the current UTC
    /// time on every toggle; clearing the marker (<paramref name="isReported"/>
    /// false) also drops the ticket reference.
    /// <para>Default implementation throws <see cref="NotSupportedException"/>
    /// so pre-existing external providers keep compiling while a write cannot
    /// silently succeed without being persisted.</para>
    /// </summary>
    Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
        => throw new NotSupportedException($"This {nameof(IMessageTrackingStore)} implementation does not support event reports.");

    /// <summary>
    /// Batched lookup of "reported" markers for the given events on
    /// <paramref name="endpointId"/>. Events that were never reported are absent
    /// from the result. Implementations answer with a single query so callers
    /// can enrich a whole search page in one round trip.
    /// <para>Default implementation returns an empty map so pre-existing
    /// external providers keep compiling; events then render as unreported.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
        => Task.FromResult<IReadOnlyDictionary<string, EventReport>>(new Dictionary<string, EventReport>());

    // Endpoint diagnostics
    Task<string> GetEndpointErrorList(string endpointId);
}
