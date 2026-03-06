using NimBus.Core.Events;
using NimBus.Core.Logging;
using NimBus.Core.Messages;
using NimBus.MessageStore;
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
    private readonly ISender _sender;
    private readonly ILogger _logger;

    public ManagerClient(ISender sender, ILogger logger = null)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
    {
        _logger?.Verbose($"MANAGER RESUBMIT EVENT: EventId: {errorResponse.EventId} EventtypeId: {eventTypeId} EventJson: {eventJson} errorResponse: {errorResponse} ");
        return _sender.Send(new Message
        {
            CorrelationId = errorResponse.CorrelationId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
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
        });
    }

    public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
    {
        _logger?.Verbose($"MANAGER SKIP EVENT: SessionId: {errorResponse.SessionId} EventId: {errorResponse.EventId} From: {errorResponse.To} ");
        return _sender.Send(new Message()
        {
            CorrelationId = errorResponse.MessageId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
            MessageType = MessageType.SkipRequest,
            MessageContent = new MessageContent(),
            ParentMessageId = errorResponse.MessageId,
            EventTypeId = eventTypeId,
            OriginatingMessageId = errorResponse.OriginatingMessageId ?? errorResponse.MessageId
        });
    }
}