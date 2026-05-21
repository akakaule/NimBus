using Azure.Messaging.ServiceBus;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Threading.Tasks;

namespace NimBus.Manager;
public interface IManagerClient
{
    /// <summary>
    /// Resolve failed event request by resubmitting/replacing it.
    /// </summary>
    /// <param name="errorResponse">ErrorResponse received from endpoint, representing the error that needs to be resolved.</param>
    /// <param name="eventTypeId">Event type that should be processed before resolving the error.</param>
    /// <param name="eventJson">Event data of that should be processed before resolving the error.</param>

    public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson);

    /// <summary>
    /// Resolve failed event request by ignoring it.
    /// </summary>
    /// <param name="errorResponse">ErrorResponse received from endpoint, representing the error that needs to be resolved.</param>
    Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId);

    /// <summary>
    /// Drive a Pending → Completed transition for a message currently parked in the
    /// PendingHandoff state. The subscriber acknowledges the request without re-invoking
    /// the user handler.
    /// </summary>
    /// <param name="pendingEntry">The pending audit entry that was projected from a PendingHandoffResponse.</param>
    /// <param name="endpoint">The subscriber endpoint that owns the pending message.</param>
    /// <param name="detailsJson">Optional JSON payload describing the completion result; carried in MessageContent.EventContent.EventJson.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="pendingEntry"/> is not in the PendingHandoff sub-status.</exception>
    [Obsolete("Use NimBus.SDK.IHandoffClient.CompleteAsync(HandoffSettlement, result) — pass the six audit-row coordinates as a typed record (EventId, SessionId, MessageId, EventTypeId, CorrelationId, OriginatingMessageId) instead of constructing a MessageEntity by hand. This overload remains for backwards compatibility.")]
    Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string detailsJson = null);

    /// <summary>
    /// Drive a Pending → Failed transition for a message currently parked in the
    /// PendingHandoff state. The supplied error text is surfaced to the subscriber's
    /// HandleHandoffFailedRequest via MessageContent.ErrorContent.
    /// </summary>
    /// <param name="pendingEntry">The pending audit entry that was projected from a PendingHandoffResponse.</param>
    /// <param name="endpoint">The subscriber endpoint that owns the pending message.</param>
    /// <param name="errorText">Human-readable error text describing the failure.</param>
    /// <param name="errorType">Optional logical error type / classifier.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="pendingEntry"/> is not in the PendingHandoff sub-status.</exception>
    [Obsolete("Use NimBus.SDK.IHandoffClient.FailAsync(HandoffSettlement, errorText, errorType) — pass the six audit-row coordinates as a typed record (EventId, SessionId, MessageId, EventTypeId, CorrelationId, OriginatingMessageId) instead of constructing a MessageEntity by hand. This overload remains for backwards compatibility.")]
    Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string errorType = null);
}

public class ManagerClient : IManagerClient
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger _logger;

    public ManagerClient(ServiceBusClient serviceBusClient, ILogger logger = null)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    public async Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
    {
        _logger?.Verbose($"MANAGER RESUBMIT EVENT: EventId: {errorResponse.EventId} EventtypeId: {eventTypeId} EventJson: {eventJson} errorResponse: {errorResponse} ");
        var message = new Message
        {
            CorrelationId = errorResponse.CorrelationId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
            From = Constants.ManagerId,
            OriginatingMessageId = errorResponse.OriginatingMessageId ?? errorResponse.MessageId,
            ParentMessageId = errorResponse.MessageId,
            MessageType = MessageType.ResubmissionRequest,
            EventTypeId = eventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson
                }
            },
        };

        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    public async Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
    {
        _logger?.Verbose($"MANAGER SKIP EVENT: SessionId: {errorResponse.SessionId} EventId: {errorResponse.EventId} From: {errorResponse.To} ");
        var message = new Message()
        {
            CorrelationId = errorResponse.MessageId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
            From = Constants.ManagerId,
            MessageType = MessageType.SkipRequest,
            MessageContent = new MessageContent(),
            ParentMessageId = errorResponse.MessageId,
            EventTypeId = eventTypeId,
            OriginatingMessageId = errorResponse.OriginatingMessageId ?? errorResponse.MessageId
        };

        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    [Obsolete("Use NimBus.SDK.IHandoffClient.CompleteAsync(HandoffSettlement, result).")]
    public async Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string detailsJson = null)
    {
        if (pendingEntry.PendingSubStatus != "Handoff")
            throw new InvalidOperationException($"CompleteHandoff requires PendingSubStatus='Handoff'; got '{pendingEntry.PendingSubStatus ?? "<null>"}' for EventId={pendingEntry.EventId}.");

        _logger?.Verbose($"MANAGER COMPLETE HANDOFF: SessionId: {pendingEntry.SessionId} EventId: {pendingEntry.EventId} Endpoint: {endpoint} ");

        var message = HandoffControlMessageFactory.CreateCompleted(CoordsFor(pendingEntry, endpoint), detailsJson);
        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    [Obsolete("Use NimBus.SDK.IHandoffClient.FailAsync(HandoffSettlement, errorText, errorType).")]
    public async Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string errorType = null)
    {
        if (pendingEntry.PendingSubStatus != "Handoff")
            throw new InvalidOperationException($"FailHandoff requires PendingSubStatus='Handoff'; got '{pendingEntry.PendingSubStatus ?? "<null>"}' for EventId={pendingEntry.EventId}.");

        _logger?.Verbose($"MANAGER FAIL HANDOFF: SessionId: {pendingEntry.SessionId} EventId: {pendingEntry.EventId} Endpoint: {endpoint} ErrorType: {errorType} ");

        var message = HandoffControlMessageFactory.CreateFailed(CoordsFor(pendingEntry, endpoint), errorText, errorType);
        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    private static HandoffSettlementCoordinates CoordsFor(MessageEntity entry, string endpoint) => new(
        To: endpoint,
        EventId: entry.EventId,
        SessionId: entry.SessionId,
        CorrelationId: entry.CorrelationId,
        ParentMessageId: entry.MessageId,
        OriginatingMessageId: entry.OriginatingMessageId,
        EventTypeId: entry.EventTypeId);
}
