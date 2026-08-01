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

    public ManagerClient(ServiceBusClient serviceBusClient, ILogger<ManagerClient> logger = null)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    /// <summary>
    /// Serilog bridge constructor. NimBus standardizes on
    /// Microsoft.Extensions.Logging (ADR-006); this overload remains for
    /// callers that still pass a Serilog logger. The logger parameter is
    /// deliberately required so single-argument construction resolves
    /// unambiguously to the MEL constructor.
    /// </summary>
    [Obsolete("Use the Microsoft.Extensions.Logging constructor — NimBus standardizes on Microsoft.Extensions.Logging (ADR-006). This bridge remains for callers that still pass a Serilog logger.")]
    public ManagerClient(ServiceBusClient serviceBusClient, Serilog.ILogger logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger is null ? null : new SerilogBridgeLogger(logger);
    }

    public async Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
    {
        _logger?.LogTrace("MANAGER RESUBMIT EVENT: EventId: {EventId} EventTypeId: {EventTypeId}", errorResponse.EventId, eventTypeId);
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

    [Obsolete("Use NimBus.SDK.IHandoffClient.CompleteAsync(HandoffSettlement, result).")]
    public async Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string detailsJson = null)
    {
        if (pendingEntry.PendingSubStatus != "Handoff")
            throw new InvalidOperationException($"CompleteHandoff requires PendingSubStatus='Handoff'; got '{pendingEntry.PendingSubStatus ?? "<null>"}' for EventId={pendingEntry.EventId}.");

        _logger?.LogTrace("MANAGER COMPLETE HANDOFF: SessionId: {SessionId} EventId: {EventId} Endpoint: {Endpoint}", pendingEntry.SessionId, pendingEntry.EventId, endpoint);

        var message = HandoffControlMessageFactory.CreateCompleted(CoordsFor(pendingEntry, endpoint), detailsJson);
        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    [Obsolete("Use NimBus.SDK.IHandoffClient.FailAsync(HandoffSettlement, errorText, errorType).")]
    public async Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string errorType = null)
    {
        if (pendingEntry.PendingSubStatus != "Handoff")
            throw new InvalidOperationException($"FailHandoff requires PendingSubStatus='Handoff'; got '{pendingEntry.PendingSubStatus ?? "<null>"}' for EventId={pendingEntry.EventId}.");

        _logger?.LogTrace("MANAGER FAIL HANDOFF: SessionId: {SessionId} EventId: {EventId} Endpoint: {Endpoint} ErrorType: {ErrorType}", pendingEntry.SessionId, pendingEntry.EventId, endpoint, errorType);

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

    /// <summary>
    /// Forwards Microsoft.Extensions.Logging calls to a caller-supplied Serilog
    /// logger. Only used by the obsolete bridge constructor.
    /// </summary>
    private sealed class SerilogBridgeLogger : ILogger
    {
        private readonly Serilog.ILogger _serilog;

        public SerilogBridgeLogger(Serilog.ILogger serilog) => _serilog = serilog;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _serilog.IsEnabled(ToSerilogLevel(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => _serilog.Write(ToSerilogLevel(logLevel), exception, "{Message}", formatter(state, exception));

        private static Serilog.Events.LogEventLevel ToSerilogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
            LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            LogLevel.Information => Serilog.Events.LogEventLevel.Information,
            LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            LogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Fatal,
        };
    }
}
