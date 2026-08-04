using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using Newtonsoft.Json;
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

}

public class ManagerClient : IManagerClient
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger _logger;

    public ManagerClient(ServiceBusClient serviceBusClient, ILogger<ManagerClient> logger = null)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    public async Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
    {
        _logger?.LogTrace("MANAGER RESUBMIT EVENT: EventId: {EventId} EventTypeId: {EventTypeId}", errorResponse.EventId, eventTypeId);
        // Marked (scheduled/timeout) entities restore the logical timeout identity
        // and the workflow conversation ID onto the resubmission clone (spec 025):
        // the handler's ScheduledMessageId-keyed guard then decides Fired vs
        // IgnoredLate. WorkflowCorrelationId falls back to the entity CorrelationId
        // for pre-upgrade audit rows; unmarked entities keep today's construction
        // byte-identical.
        var isMarked = !string.IsNullOrEmpty(errorResponse.ScheduledMessageId);
        var message = new Message
        {
            CorrelationId = isMarked
                ? errorResponse.WorkflowCorrelationId ?? errorResponse.CorrelationId
                : errorResponse.CorrelationId,
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
            ScheduledMessageId = errorResponse.ScheduledMessageId,
            ScheduledEnqueueTimeUtc = errorResponse.ScheduledEnqueueTimeUtc,
        };

        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    public async Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
    {
        _logger?.LogTrace("MANAGER SKIP EVENT: SessionId: {SessionId} EventId: {EventId} From: {From}", errorResponse.SessionId, errorResponse.EventId, errorResponse.To);
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

}
