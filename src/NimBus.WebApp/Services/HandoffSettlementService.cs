using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services;

/// <summary>
/// Default <see cref="IHandoffSettlementService"/>. See the interface for the
/// contract; this implementation always writes the audit row after a successful
/// settlement so no settle path can silently skip it.
/// </summary>
public sealed class HandoffSettlementService : IHandoffSettlementService
{
    private const string HandoffSubStatus = "Handoff";

    private readonly INimBusMessageStore _store;
    private readonly IAuditLogService _audit;
    private readonly ILogger<HandoffSettlementService> _logger;

    /// <summary>Initialises the service with the message store, audit log, and logger.</summary>
    public HandoffSettlementService(
        INimBusMessageStore store,
        IAuditLogService audit,
        ILogger<HandoffSettlementService> logger)
    {
        _store = store;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IActionResult> SettleAsync(
        string endpointId,
        string eventId,
        string messageId,
        MessageAuditType auditType,
        string? auditComment,
        string auditorName,
        HttpContext? httpContext,
        Func<MessageEntity, string, Task> settle,
        CancellationToken cancellationToken = default)
    {
        UnresolvedEvent pendingEvent;
        try
        {
            pendingEvent = await _store.GetEvent(endpointId, eventId);
        }
        // Only genuinely-not-found signals map to 404: the endpoint container is
        // absent, or Cosmos reports the item missing. Transient/unexpected store
        // faults (throttling 429, connectivity) must NOT masquerade as "handoff
        // gone" — let them propagate to a 500 so the caller can retry.
        catch (EndpointNotFoundException e)
        {
            _logger.LogWarning("Handoff settle: endpoint/event not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpointId, eventId, e.Message);
            return new NotFoundObjectResult("Event not found");
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Handoff settle: event container not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpointId, eventId, e.Message);
            return new NotFoundObjectResult("Event not found");
        }

        if (pendingEvent == null)
            return new NotFoundObjectResult("Event not found");

        if (pendingEvent.ResolutionStatus != ResolutionStatus.Pending
            || !string.Equals(pendingEvent.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Handoff settle rejected: event is not a pending handoff. EndpointId: {EndpointId}, EventId: {EventId}, Status: {Status}, SubStatus: {SubStatus}",
                endpointId, eventId, pendingEvent.ResolutionStatus, pendingEvent.PendingSubStatus ?? "<null>");
            return new BadRequestObjectResult("Event is not a pending handoff.");
        }

        var pendingEntry = new MessageEntity
        {
            EventId = pendingEvent.EventId,
            MessageId = messageId,
            SessionId = pendingEvent.SessionId,
            CorrelationId = pendingEvent.CorrelationId,
            OriginatingMessageId = pendingEvent.OriginatingMessageId,
            EventTypeId = pendingEvent.EventTypeId,
            PendingSubStatus = pendingEvent.PendingSubStatus,
        };

        _logger.LogInformation(
            "Handoff settle ({AuditType}). EndpointId: {EndpointId}, EventId: {EventId}, MessageId: {MessageId}, Auditor: {Auditor}",
            auditType, endpointId, eventId, messageId, auditorName);

        await settle(pendingEntry, auditorName);

        await _audit.LogAuditAsync(auditType, httpContext,
            data: auditComment, eventId: eventId, endpointId: endpointId,
            eventTypeId: pendingEvent.EventTypeId, auditorNameOverride: auditorName,
            cancellationToken: cancellationToken);

        return new OkResult();
    }
}
