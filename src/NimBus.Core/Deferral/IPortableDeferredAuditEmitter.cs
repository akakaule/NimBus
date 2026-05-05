using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Core.Deferral;

/// <summary>
/// Emits the five park-and-replay audit types
/// (<see cref="MessageAuditType.Parked"/>,
/// <see cref="MessageAuditType.ReplayStarted"/>,
/// <see cref="MessageAuditType.Replayed"/>,
/// <see cref="MessageAuditType.ReplayCompleted"/>,
/// <see cref="MessageAuditType.ReplaySkippedByOperator"/>) into
/// <see cref="IMessageTrackingStore"/>'s message-audits store. Both the portable
/// (<see cref="PortableDeferredMessageProcessor"/>) and the Service-Bus
/// warm-path wrapper share this emitter so the audit rows are byte-identical
/// across transports — the FR-054 / NFR-011 contract.
///
/// The single point of truth for audit-row shape lives in the default
/// implementation; alternative emitters exist only for testability.
///
/// See <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
/// §7 for the per-event audit shapes.
/// </summary>
public interface IPortableDeferredAuditEmitter
{
    /// <summary>
    /// Records a <see cref="MessageAuditType.Parked"/> audit row against the
    /// parked message's EventId. Comment shape:
    /// <c>"Parked at endpoint {endpointId}, session {sessionKey}, sequence
    /// {parkSequence}, blockedBy {blockingEventId}"</c>.
    /// </summary>
    Task EmitParkedAsync(ParkedMessage parked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a <see cref="MessageAuditType.ReplayStarted"/> audit row against
    /// the BLOCKING event's EventId — the event whose successful re-handling
    /// triggered the replay. Comment shape:
    /// <c>"Replay started: endpoint {endpointId}, session {sessionKey}, count
    /// {activeParkCount}"</c>.
    /// </summary>
    Task EmitReplayStartedAsync(string endpointId, string sessionKey, string blockingEventId, int activeParkCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a <see cref="MessageAuditType.Replayed"/> audit row against the
    /// parked message's EventId. Comment shape:
    /// <c>"Replayed at endpoint {endpointId}, session {sessionKey}, sequence
    /// {parkSequence}"</c>.
    /// </summary>
    Task EmitReplayedAsync(ParkedMessage parked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a <see cref="MessageAuditType.ReplayCompleted"/> audit row
    /// against the BLOCKING event's EventId. Comment shape:
    /// <c>"Replay completed: endpoint {endpointId}, session {sessionKey}, count
    /// {replayedCount}"</c>.
    /// </summary>
    Task EmitReplayCompletedAsync(string endpointId, string sessionKey, string blockingEventId, int replayedCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a <see cref="MessageAuditType.ReplaySkippedByOperator"/> audit
    /// row against each parked message's EventId. <paramref name="operatorId"/>
    /// is the actor (recorded as <c>AuditorName</c>). <paramref name="comment"/>
    /// is the operator-supplied note (or <c>"Skipped via nimbus-ops"</c> when
    /// blank).
    /// </summary>
    Task EmitSkippedByOperatorAsync(ParkedMessage parked, string operatorId, string? comment, CancellationToken cancellationToken = default);
}
