using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore.Abstractions;
using Newtonsoft.Json;
using NimBus.MessageStore;

namespace NimBus.Core.Deferral;

/// <summary>
/// Transport-agnostic deferred-by-session park-and-replay implementation.
/// Implements <see cref="IDeferredMessageProcessor.ProcessDeferredMessagesAsync"/>
/// by:
/// <list type="number">
///   <item>Reading the per-session checkpoint from <see cref="ISessionStateStore.GetLastReplayedSequence"/>.</item>
///   <item>Batch-fetching active parked rows after the checkpoint from
///         <see cref="IParkedMessageStore.GetActiveAsync"/>, ordered by
///         <c>ParkSequence ASC</c>.</item>
///   <item>For each row in order: deserializing the envelope, sending via
///         <see cref="ISender.Send(IMessage,int,CancellationToken)"/>, marking
///         the row replayed, emitting the per-row Replayed audit, and
///         conditionally advancing the checkpoint via
///         <see cref="ISessionStateStore.TryAdvanceLastReplayedSequence"/>.</item>
///   <item>Looping until the batch is short, then re-querying the active count
///         to catch late parks before declaring the session drained — see
///         design §6.3.</item>
/// </list>
///
/// Crash resilience comes from the forward-only checkpoint plus the
/// active-only filter on <c>GetActiveAsync</c>: a crash between Send and
/// MarkReplayed leaves the row active and re-replays it on restart (resolver
/// idempotency absorbs the duplicate); a crash between MarkReplayed and
/// TryAdvance leaves the checkpoint stale but the row is filtered out by the
/// active-only predicate, so replay continues from the next row and the
/// checkpoint catches up on the next successful advance.
///
/// Concurrent replayers serialize via the conditional checkpoint advance: the
/// loser observes <c>TryAdvance returns false</c>, re-reads the checkpoint, and
/// loops with the new value. Duplicate Send is absorbed by resolver
/// idempotency. See design §5.3.
///
/// See <c>docs/specs/003-rabbitmq-transport/deferred-by-session-design.md</c>
/// for the full design context.
/// </summary>
public sealed class PortableDeferredMessageProcessor : IDeferredMessageProcessor
{
    private const int DefaultBatchSize = 100;

    private readonly ISender _sender;
    private readonly IParkedMessageStore _parkedStore;
    private readonly ISessionStateStore _sessionStateStore;
    private readonly IPortableDeferredAuditEmitter _auditEmitter;

    public PortableDeferredMessageProcessor(
        ISender sender,
        IParkedMessageStore parkedStore,
        ISessionStateStore sessionStateStore,
        IPortableDeferredAuditEmitter auditEmitter)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _parkedStore = parkedStore ?? throw new ArgumentNullException(nameof(parkedStore));
        _sessionStateStore = sessionStateStore ?? throw new ArgumentNullException(nameof(sessionStateStore));
        _auditEmitter = auditEmitter ?? throw new ArgumentNullException(nameof(auditEmitter));
    }

    /// <summary>
    /// Parks <paramref name="message"/> for later replay when its session unblocks.
    /// Idempotent on <c>(EndpointId, MessageId)</c>: a re-delivered message that
    /// is already parked is a no-op (no double audit, no double row). Emits the
    /// <see cref="MessageAuditType.Parked"/> audit only on the fresh-park path.
    ///
    /// <paramref name="endpointId"/> is the receiver endpoint name (the
    /// <c>To</c> property on the inbound transport message).
    /// <paramref name="sessionKey"/> is the application-level session id.
    /// <paramref name="blockingEventId"/> is the EventId of the failed message
    /// that blocked the session — recorded on the parked row for operator
    /// diagnostics.
    /// </summary>
    public async Task<long> ParkAsync(
        string endpointId,
        string sessionKey,
        string blockingEventId,
        IReceivedMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = ToMessageEntity(endpointId, message);
        var parked = new ParkedMessage
        {
            EndpointId = endpointId,
            SessionKey = sessionKey,
            MessageId = message.MessageId,
            EventId = message.EventId,
            EventTypeId = message.EventTypeId ?? string.Empty,
            BlockingEventId = blockingEventId,
            MessageEnvelopeJson = JsonConvert.SerializeObject(envelope),
            ParkedAtUtc = DateTime.UtcNow,
        };

        // Compare CountActive before / after to detect whether the park was a
        // fresh insert or a duplicate. The store doesn't expose a "was-fresh"
        // signal directly; the active-park counter delta is the cleanest
        // observable. (CountActiveAsync is the reconciliation source for
        // ISessionStateStore.GetActiveParkCount per the design — its behaviour
        // is the authoritative ground truth for "did this row become active?".)
        var preCount = await _parkedStore.CountActiveAsync(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);
        var sequence = await _parkedStore.ParkAsync(parked, cancellationToken).ConfigureAwait(false);
        var postCount = await _parkedStore.CountActiveAsync(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);

        if (postCount > preCount)
        {
            // Fresh park — emit the audit. Store the message-entity envelope on
            // the parked.MessageEnvelopeJson field so we can deserialize back to
            // a Message at replay time.
            await _auditEmitter.EmitParkedAsync(parked, cancellationToken).ConfigureAwait(false);
        }
        // else: duplicate park, no second audit row, no second active-park
        // increment. The store-level idempotency check did its job.

        return sequence;
    }

    /// <summary>
    /// Drains the active parked rows for <paramref name="sessionId"/> at
    /// <paramref name="topicName"/>, replaying each via <see cref="ISender.Send(IMessage,int,CancellationToken)"/>
    /// in <c>ParkSequence ASC</c> order. Resilient to crash mid-replay; safe
    /// under concurrent invocation. Emits ReplayStarted / Replayed / ReplayCompleted
    /// audits per design §7.
    ///
    /// <paramref name="topicName"/> maps to the receiver endpoint id (each
    /// NimBus topic is one endpoint).
    /// </summary>
    public async Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Topic name is required.", nameof(topicName));

        var endpointId = topicName;
        var sessionKey = sessionId;

        // Pre-check: nothing to replay. Skip the audit + loop entirely so
        // tick-driven invocations on idle sessions stay cheap.
        var initialActive = await _parkedStore.CountActiveAsync(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);
        if (initialActive == 0) return;

        // The "blocking event id" we tag ReplayStarted/Completed with is the
        // BlockingEventId of the first active row — that's the event whose
        // resolution unblocked the session and triggered this replay.
        var firstBatch = await _parkedStore.GetActiveAsync(endpointId, sessionKey, afterSequence: -1, limit: 1, cancellationToken).ConfigureAwait(false);
        if (firstBatch.Count == 0) return; // race: rows transitioned terminal between count + read.
        var blockingEventId = firstBatch[0].BlockingEventId ?? firstBatch[0].EventId;

        await _auditEmitter.EmitReplayStartedAsync(endpointId, sessionKey, blockingEventId, initialActive, cancellationToken).ConfigureAwait(false);

        var checkpoint = await _sessionStateStore.GetLastReplayedSequence(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);
        var replayedCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _parkedStore.GetActiveAsync(endpointId, sessionKey, afterSequence: checkpoint, limit: DefaultBatchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                // Possibly drained — but a parker may have raced just-now. Re-
                // check the active count before declaring drained per design §6.3.
                var remaining = await _parkedStore.CountActiveAsync(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);
                if (remaining == 0) break;
                // Newer parks landed; loop back to GetActiveAsync at the same
                // checkpoint (which will pick them up since their ParkSequence
                // > checkpoint by allocation order).
                continue;
            }

            foreach (var parked in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Send the republished message. Throws on transport error;
                //    the loop exits without advancing the checkpoint, and the
                //    next ProcessDeferred tick retries from the same checkpoint.
                var message = DeserializeEnvelope(parked.MessageEnvelopeJson);
                await _sender.Send(message, cancellationToken: cancellationToken).ConfigureAwait(false);

                // 2. Mark the parked row replayed (terminal state). Idempotent.
                //    Active-park counter on SessionStates decrements here.
                await _parkedStore.MarkReplayedAsync(parked.EndpointId, parked.MessageId, cancellationToken).ConfigureAwait(false);

                // 3. Emit the per-row Replayed audit BEFORE advancing the
                //    checkpoint. If a crash lands between MarkReplayed and the
                //    audit, the next replay tick won't re-emit the audit
                //    because the row is already terminal — small audit gap,
                //    acceptable trade-off for the design's "always-make-forward-
                //    progress" invariant.
                await _auditEmitter.EmitReplayedAsync(parked, cancellationToken).ConfigureAwait(false);

                // 4. Conditionally advance the checkpoint. Loser of a concurrent
                //    advance just falls through — re-reads happen at the top of
                //    the next loop iteration via GetLastReplayedSequence... no,
                //    actually we keep our local checkpoint forward-only too:
                //    if the conditional advance fails, the OTHER replayer has
                //    advanced past us, so our local checkpoint is stale; re-read
                //    it and continue.
                var advanced = await _sessionStateStore.TryAdvanceLastReplayedSequence(
                    endpointId, sessionKey,
                    expectedCurrent: checkpoint, newValue: (int)parked.ParkSequence,
                    cancellationToken).ConfigureAwait(false);
                if (advanced)
                {
                    checkpoint = (int)parked.ParkSequence;
                }
                else
                {
                    // Concurrent replayer raced ahead. Re-read and abandon this
                    // batch; the next iteration's GetActiveAsync starts after
                    // the new checkpoint.
                    checkpoint = await _sessionStateStore.GetLastReplayedSequence(endpointId, sessionKey, cancellationToken).ConfigureAwait(false);
                    break;
                }

                replayedCount++;
            }

            if (batch.Count < DefaultBatchSize)
            {
                // Likely drained — but the design's drained-check loops back to
                // GetActiveAsync (handled at the top of the while). Setting
                // checkpoint to the last seen ParkSequence is enough.
                continue;
            }
        }

        await _auditEmitter.EmitReplayCompletedAsync(endpointId, sessionKey, blockingEventId, replayedCount, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Operator-driven skip: marks the supplied parked rows as
    /// <see cref="MessageAuditType.ReplaySkippedByOperator"/> and emits per-row
    /// audits. Used by the WebApp's "skip parked" affordance.
    /// </summary>
    public async Task SkipParkedAsync(
        string endpointId,
        string sessionKey,
        IReadOnlyList<ParkedMessage> rows,
        string operatorId,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        var messageIds = new List<string>(rows.Count);
        foreach (var row in rows) messageIds.Add(row.MessageId);

        await _parkedStore.MarkSkippedAsync(endpointId, sessionKey, messageIds, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await _auditEmitter.EmitSkippedByOperatorAsync(row, operatorId, comment, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MessageEntity ToMessageEntity(string endpointId, IReceivedMessage message)
    {
        return new MessageEntity
        {
            EndpointId = endpointId,
            EventId = message.EventId,
            MessageId = message.MessageId,
            SessionId = message.SessionId,
            CorrelationId = message.CorrelationId,
            From = message.From,
            To = message.To,
            OriginatingFrom = message.OriginatingFrom,
            OriginatingMessageId = message.OriginatingMessageId,
            ParentMessageId = message.ParentMessageId,
            MessageContent = message.MessageContent,
            MessageType = message.MessageType,
            EventTypeId = message.EventTypeId,
            OriginalSessionId = message.OriginalSessionId,
            DeferralSequence = message.DeferralSequence,
            EnqueuedTimeUtc = message.EnqueuedTimeUtc,
            RetryCount = message.RetryCount,
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,
            QueueTimeMs = message.QueueTimeMs,
            ProcessingTimeMs = message.ProcessingTimeMs,
        };
    }

    private static IMessage DeserializeEnvelope(string envelopeJson)
    {
        if (string.IsNullOrEmpty(envelopeJson))
            throw new InvalidMessageException("Parked-message envelope JSON is empty; cannot deserialize for replay.");

        var entity = JsonConvert.DeserializeObject<MessageEntity>(envelopeJson)
            ?? throw new InvalidMessageException("Parked-message envelope JSON deserialized to null.");

        // Project MessageEntity (which is IReceivedMessage-shaped) onto the
        // outbound IMessage shape. ParkedMessages always represent a single
        // re-publish; we don't carry timing properties (the receiver re-measures
        // on the replay).
        return new Message
        {
            EventId = entity.EventId,
            MessageId = entity.MessageId,
            SessionId = entity.SessionId,
            CorrelationId = entity.CorrelationId,
            From = entity.From,
            To = entity.To,
            OriginatingFrom = entity.OriginatingFrom,
            OriginatingMessageId = entity.OriginatingMessageId,
            ParentMessageId = entity.ParentMessageId,
            MessageContent = entity.MessageContent,
            MessageType = entity.MessageType,
            EventTypeId = entity.EventTypeId,
            OriginalSessionId = entity.OriginalSessionId,
            DeferralSequence = entity.DeferralSequence,
            RetryCount = entity.RetryCount,
            DeadLetterReason = entity.DeadLetterReason,
            DeadLetterErrorDescription = entity.DeadLetterErrorDescription,
        };
    }
}
