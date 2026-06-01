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

    // Filtered queries
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
    Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount);

    // Audit trail
    Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null);
    Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId);
    Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount);

    // Endpoint diagnostics
    Task<string> GetEndpointErrorList(string endpointId);
}
