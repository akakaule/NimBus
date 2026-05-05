using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Core.Deferral;

/// <summary>
/// Default <see cref="IPortableDeferredAuditEmitter"/> — formats the five
/// park-and-replay audit comments per <c>docs/specs/003-rabbitmq-transport/
/// deferred-by-session-design.md</c> §7.1 and writes them via
/// <see cref="IMessageTrackingStore.StoreMessageAudit"/>. Single source of truth
/// for audit-row shape: every transport (portable + Service Bus warm-path
/// wrapper) routes through this class so audit rows are byte-identical across
/// transports.
/// </summary>
public sealed class DefaultPortableDeferredAuditEmitter : IPortableDeferredAuditEmitter
{
    private const string SystemActorName = "NimBus";
    private const string DefaultOperatorComment = "Skipped via nimbus-ops";

    private readonly IMessageTrackingStore _trackingStore;

    public DefaultPortableDeferredAuditEmitter(IMessageTrackingStore trackingStore)
    {
        _trackingStore = trackingStore ?? throw new ArgumentNullException(nameof(trackingStore));
    }

    public Task EmitParkedAsync(ParkedMessage parked, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parked);
        var blocked = string.IsNullOrEmpty(parked.BlockingEventId) ? "(unknown)" : parked.BlockingEventId;
        var comment = string.Format(
            CultureInfo.InvariantCulture,
            "Parked at endpoint {0}, session {1}, sequence {2}, blockedBy {3}",
            parked.EndpointId, parked.SessionKey, parked.ParkSequence, blocked);
        return WriteAudit(parked.EventId, MessageAuditType.Parked, SystemActorName, comment,
            parked.EndpointId, parked.EventTypeId);
    }

    public Task EmitReplayStartedAsync(string endpointId, string sessionKey, string blockingEventId, int activeParkCount, CancellationToken cancellationToken = default)
    {
        var comment = string.Format(
            CultureInfo.InvariantCulture,
            "Replay started: endpoint {0}, session {1}, count {2}",
            endpointId, sessionKey, activeParkCount);
        return WriteAudit(blockingEventId, MessageAuditType.ReplayStarted, SystemActorName, comment,
            endpointId, eventTypeId: null);
    }

    public Task EmitReplayedAsync(ParkedMessage parked, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parked);
        var comment = string.Format(
            CultureInfo.InvariantCulture,
            "Replayed at endpoint {0}, session {1}, sequence {2}",
            parked.EndpointId, parked.SessionKey, parked.ParkSequence);
        return WriteAudit(parked.EventId, MessageAuditType.Replayed, SystemActorName, comment,
            parked.EndpointId, parked.EventTypeId);
    }

    public Task EmitReplayCompletedAsync(string endpointId, string sessionKey, string blockingEventId, int replayedCount, CancellationToken cancellationToken = default)
    {
        var comment = string.Format(
            CultureInfo.InvariantCulture,
            "Replay completed: endpoint {0}, session {1}, count {2}",
            endpointId, sessionKey, replayedCount);
        return WriteAudit(blockingEventId, MessageAuditType.ReplayCompleted, SystemActorName, comment,
            endpointId, eventTypeId: null);
    }

    public Task EmitSkippedByOperatorAsync(ParkedMessage parked, string operatorId, string? comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parked);
        var note = string.IsNullOrWhiteSpace(comment) ? DefaultOperatorComment : comment;
        return WriteAudit(parked.EventId, MessageAuditType.ReplaySkippedByOperator,
            string.IsNullOrEmpty(operatorId) ? SystemActorName : operatorId, note,
            parked.EndpointId, parked.EventTypeId);
    }

    private Task WriteAudit(string eventId, MessageAuditType auditType, string auditorName, string comment, string? endpointId, string? eventTypeId)
    {
        var entity = new MessageAuditEntity
        {
            AuditorName = auditorName,
            AuditTimestamp = DateTime.UtcNow,
            AuditType = auditType,
            Comment = comment,
        };
        return _trackingStore.StoreMessageAudit(eventId, entity, endpointId, eventTypeId);
    }
}
