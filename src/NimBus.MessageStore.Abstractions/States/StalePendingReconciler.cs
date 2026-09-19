using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.Core.Messages;

namespace NimBus.MessageStore;

/// <summary>
/// Why a Pending audit row was, or was not, selected for repair (Spec 032 §4.2, from Spec 030 §9
/// step 2). The order is the order the ladder tests them in: the first match wins.
/// </summary>
public enum StalePendingVerdict
{
    /// <summary>
    /// The incident signature: a clean <see cref="MessageType.ResolutionResponse"/> from the endpoint
    /// precedes the request copy that wrote the row, and nothing but request copies follow it.
    /// </summary>
    Repairable,

    /// <summary>Nothing is stored for the event — the history has aged out or was purged.</summary>
    HistoryMissing,

    /// <summary>No terminal response in the row's session: the event is genuinely in flight.</summary>
    NoTerminal,

    /// <summary>The latest terminal is an <see cref="MessageType.ErrorResponse"/>: operator decision, not a repair.</summary>
    LatestTerminalIsError,

    /// <summary>The latest terminal is a <see cref="MessageType.SkipResponse"/>: the row should read Skipped, not Completed.</summary>
    LatestTerminalIsSkip,

    /// <summary>
    /// The <see cref="MessageType.ResolutionResponse"/> carries a dead-letter description (a replayed
    /// dead-lettered copy). <c>ResolverService.GetResultingStatus</c> projects such a message as
    /// DeadLettered, never Completed.
    /// </summary>
    LatestTerminalIsDeadLettered,

    /// <summary>The response is not older than the row's request: the row is a newer attempt, still in flight.</summary>
    ResponseNotBeforeRow,

    /// <summary>
    /// A control request or another non-request message followed the response. For a row written by a
    /// control copy this is always its own copy, so control-written rows land here by design: an
    /// operator decides, the reconcile never repairs them.
    /// </summary>
    LaterControlMessage,

    /// <summary>
    /// A request copy newer than the row is stored but not projected onto it yet. Repairing would only
    /// be undone when that copy is processed.
    /// </summary>
    LaterRequestCopy,
}

/// <summary>One candidate row and the verdict its stored history produces.</summary>
/// <param name="Row">The Pending audit row.</param>
/// <param name="Verdict">Why it is, or is not, repairable.</param>
/// <param name="Response">The <see cref="MessageType.ResolutionResponse"/> a repair would apply, when there is one.</param>
/// <param name="LatestTerminal">The latest terminal message in the row's session, when there is one.</param>
/// <param name="Detail">A human-readable sentence naming the messages and times the verdict rests on.</param>
public sealed record StalePendingClassification(
    UnresolvedEvent Row,
    StalePendingVerdict Verdict,
    MessageEntity? Response,
    MessageEntity? LatestTerminal,
    string Detail)
{
    /// <summary>True when the reconcile may replace this row with the Completed projection.</summary>
    public bool IsRepairable => Verdict == StalePendingVerdict.Repairable;
}

/// <summary>
/// Decides which stale Pending rows can be repaired from the outcome the Resolver already stored, and
/// rebuilds the Completed projection it would have written (Spec 032).
///
/// <para>Pure and provider-neutral: it reads an audit row plus that event's per-message history and
/// returns a verdict. The store primitive that applies the result is
/// <c>IMessageTrackingStore.TryCompletePendingMessage</c>.</para>
///
/// <para><strong>It never invents a status.</strong> The only terminal it can produce is one the
/// Resolver already recorded as a <see cref="MessageType.ResolutionResponse"/> for that event and
/// session. Everything else is classified and left to a human.</para>
/// </summary>
public static class StalePendingReconciler
{
    /// <summary>
    /// Row message types the preview selects. Control-request rows are included because a rescheduled
    /// control copy corrupts a row the same way an <see cref="MessageType.EventRequest"/> copy does
    /// (Spec 030 §9 step 1) — they are listed for the operator, never repaired automatically.
    /// </summary>
    public static readonly IReadOnlyList<MessageType> CandidateRowTypes = new[]
    {
        MessageType.EventRequest,
        MessageType.ResubmissionRequest,
        MessageType.SkipRequest,
        MessageType.HandoffCompletedRequest,
        MessageType.HandoffFailedRequest,
    };

    private static readonly HashSet<MessageType> TerminalTypes = new()
    {
        MessageType.ResolutionResponse,
        MessageType.ErrorResponse,
        MessageType.SkipResponse,
    };

    /// <summary>
    /// True when <paramref name="row"/> is a row the preview should classify: Pending, not a parked
    /// handoff (those are genuinely in flight), written by one of <see cref="CandidateRowTypes"/>.
    /// </summary>
    public static bool IsCandidate(UnresolvedEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.ResolutionStatus == ResolutionStatus.Pending
            && string.IsNullOrEmpty(row.PendingSubStatus)
            && CandidateRowTypes.Contains(row.MessageType);
    }

    /// <summary>
    /// Spec 030 §9 step 2. Only messages in the row's session that concern the endpoint count (plus
    /// any <see cref="MessageType.SkipResponse"/>). The latest terminal response from the endpoint
    /// decides: it must be a <see cref="MessageType.ResolutionResponse"/> enqueued before the request
    /// copy that wrote the row, and nothing but request copies may follow it. Every other shape is
    /// left for an operator.
    /// </summary>
    /// <param name="row">The candidate audit row.</param>
    /// <param name="history">Every stored message for the row's event, in any order — including, in a fan-out, other endpoints' copies.</param>
    /// <param name="endpointId">The endpoint that owns the row; only its messages are judged and terminals must come from it.</param>
    public static StalePendingClassification Classify(
        UnresolvedEvent row,
        IReadOnlyCollection<MessageEntity> history,
        string endpointId)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return new StalePendingClassification(row, StalePendingVerdict.HistoryMissing, null, null,
                "no messages stored for this event");
        }

        // History is per event, not per endpoint: in a fan-out every subscriber's request copy and
        // response share the event id AND the publisher's session id. Keep only the messages that
        // concern this endpoint (the Resolver stamps EndpointId as To for requests, From for
        // responses), or a sibling's response would read as a "later control message" and its
        // request copy as a "later request copy", and no fan-out event could ever be repaired.
        // A SkipResponse is issued on the operator's behalf, so its EndpointId is the manager's;
        // it still means this row must not become Completed, so it stays in scope regardless.
        // Cosmos and the in-memory store return history unordered, SQL Server ordered. Sort so the
        // verdict cannot depend on the provider.
        var session = history
            .Where(m => SameSession(m.SessionId, row.SessionId))
            .Where(m => m.MessageType == MessageType.SkipResponse || ConcernsEndpoint(m, endpointId))
            .OrderBy(m => m.EnqueuedTimeUtc)
            .ToList();

        // A SkipResponse is issued on the operator's behalf, so its From is not the endpoint; it still
        // means the row must not become Completed, so it counts regardless of its sender.
        var terminals = session
            .Where(m => TerminalTypes.Contains(m.MessageType)
                        && (m.MessageType == MessageType.SkipResponse || IsFrom(m, endpointId)))
            .ToList();

        if (terminals.Count == 0)
        {
            return new StalePendingClassification(row, StalePendingVerdict.NoTerminal, null, null,
                $"{session.Count} message(s) in the session, no terminal response from {endpointId}");
        }

        var latest = terminals[^1];
        switch (latest.MessageType)
        {
            case MessageType.ErrorResponse:
                return new StalePendingClassification(row, StalePendingVerdict.LatestTerminalIsError, null, latest,
                    $"latest terminal is ErrorResponse {latest.MessageId} at {latest.EnqueuedTimeUtc:O}");
            case MessageType.SkipResponse:
                return new StalePendingClassification(row, StalePendingVerdict.LatestTerminalIsSkip, null, latest,
                    $"latest terminal is SkipResponse {latest.MessageId} at {latest.EnqueuedTimeUtc:O}");
            default:
                break;
        }

        // Null OR empty: the SQL Server provider maps a NULL column to string.Empty on read, Cosmos
        // and the in-memory store return null. Without this, no SQL Server row could ever be
        // repaired - found by driving the card against a live SQL-backed AppHost.
        if (!string.IsNullOrEmpty(latest.DeadLetterErrorDescription))
        {
            return new StalePendingClassification(row, StalePendingVerdict.LatestTerminalIsDeadLettered, null, latest,
                $"ResolutionResponse {latest.MessageId} at {latest.EnqueuedTimeUtc:O} carries DeadLetterErrorDescription " +
                $"(reason '{latest.DeadLetterReason}'); the Resolver projects it as DeadLettered");
        }

        if (latest.EnqueuedTimeUtc >= row.EnqueuedTimeUtc)
        {
            return new StalePendingClassification(row, StalePendingVerdict.ResponseNotBeforeRow, latest, latest,
                $"ResolutionResponse {latest.MessageId} at {latest.EnqueuedTimeUtc:O} is not before the row's request at {row.EnqueuedTimeUtc:O}");
        }

        var afterResponse = session.Where(m => m.EnqueuedTimeUtc > latest.EnqueuedTimeUtc).ToList();

        var control = afterResponse.Where(m => m.MessageType != MessageType.EventRequest).ToList();
        if (control.Count > 0)
        {
            return new StalePendingClassification(row, StalePendingVerdict.LaterControlMessage, latest, latest,
                "after the response: " + string.Join("; ",
                    control.Select(m => $"{m.MessageType} {m.MessageId} at {m.EnqueuedTimeUtc:O}")));
        }

        // Keyed on time alone, deliberately. The reference tool also required a different MessageId,
        // but Spec 030 §5.7 made ScheduleRedelivery keep the original id, so a stored copy may share
        // the row's LastMessageId — comparing ids would miss exactly the copies 3.7.0 produces.
        var unprojectedCopy = afterResponse.FirstOrDefault(m => m.EnqueuedTimeUtc > row.EnqueuedTimeUtc);
        if (unprojectedCopy is not null)
        {
            return new StalePendingClassification(row, StalePendingVerdict.LaterRequestCopy, latest, latest,
                $"request copy {unprojectedCopy.MessageId} at {unprojectedCopy.EnqueuedTimeUtc:O} is newer than the row and not projected yet");
        }

        return new StalePendingClassification(row, StalePendingVerdict.Repairable, latest, latest,
            $"stale {row.MessageType} {row.LastMessageId} at {row.EnqueuedTimeUtc:O} overtook " +
            $"ResolutionResponse {latest.MessageId} at {latest.EnqueuedTimeUtc:O}");
    }

    /// <summary>
    /// Rebuilds the <see cref="UnresolvedEvent"/> field for field as <c>ResolverService.CreateUnresolvedEvent</c>
    /// would have built it from the stored <see cref="MessageEntity"/> of the ResolutionResponse
    /// (Spec 030 §9 step 3).
    /// </summary>
    /// <param name="response">The response the row should have reflected.</param>
    /// <param name="history">Every stored message for the event, used only for the handoff wall clock.</param>
    /// <param name="endpointId">Fallback when the stored response carries no <c>EndpointId</c>.</param>
    /// <param name="utcNow">Stamped as <c>UpdatedAt</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="response"/> is not a clean <see cref="MessageType.ResolutionResponse"/>. Only the
    /// verdict decides what may be applied; this is the guard against calling it on anything else.
    /// </exception>
    public static UnresolvedEvent BuildCompletedProjection(
        MessageEntity response,
        IReadOnlyCollection<MessageEntity> history,
        string endpointId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(history);

        if (response.MessageType != MessageType.ResolutionResponse || !string.IsNullOrEmpty(response.DeadLetterErrorDescription))
        {
            throw new InvalidOperationException(
                $"Only a ResolutionResponse without a dead-letter description projects to Completed; got {response.MessageType} {response.MessageId}.");
        }

        // ResolverService.ComputeHandoffWallClockMsIfTerminal: for an event that went through async
        // handoff the terminal row carries the wall-clock span since the original EventRequest. The
        // Resolver measures it at write time, which a repair cannot reproduce; the response's enqueue
        // time is the deterministic stand-in (Spec 032 §4.2).
        long? processingTimeOverride = null;
        if (history.Any(m => m.MessageType == MessageType.PendingHandoffResponse))
        {
            var eventRequest = history
                .Where(m => m.MessageType == MessageType.EventRequest)
                .MinBy(m => m.EnqueuedTimeUtc);
            if (eventRequest is not null)
            {
                processingTimeOverride = (long)Math.Max(0, (response.EnqueuedTimeUtc - eventRequest.EnqueuedTimeUtc).TotalMilliseconds);
            }
        }

        return new UnresolvedEvent
        {
            UpdatedAt = utcNow,
            EnqueuedTimeUtc = response.EnqueuedTimeUtc,

            EventId = response.EventId,
            SessionId = response.SessionId,
            CorrelationId = response.CorrelationId,

            ResolutionStatus = ResolutionStatus.Completed,
            EndpointRole = response.EndpointRole,
            EndpointId = string.IsNullOrEmpty(response.EndpointId) ? endpointId : response.EndpointId,
            RetryCount = response.RetryCount,
            RetryLimit = response.RetryLimit,
            MessageType = response.MessageType,
            // The Resolver writes these from the live message, where they are null on a clean
            // response. A history row read back from SQL Server carries string.Empty instead;
            // normalise so the repaired row is byte-for-byte what the Resolver would have written.
            DeadLetterReason = NullIfEmpty(response.DeadLetterReason),
            DeadLetterErrorDescription = NullIfEmpty(response.DeadLetterErrorDescription),

            LastMessageId = response.MessageId,
            OriginatingMessageId = response.OriginatingMessageId,
            ParentMessageId = response.ParentMessageId,
            // CreateUnresolvedEvent reads the skip reason only for a SkipResponse; a ResolutionResponse
            // carries the dead-letter description, which is null on this path.
            Reason = NullIfEmpty(response.DeadLetterErrorDescription),
            OriginatingFrom = response.OriginatingFrom,

            EventTypeId = response.EventTypeId,
            To = response.To,
            From = response.From,
            MessageContent = response.MessageContent,
            QueueTimeMs = response.QueueTimeMs,
            ProcessingTimeMs = processingTimeOverride ?? response.ProcessingTimeMs,
            PendingSubStatus = response.PendingSubStatus,
            HandoffReason = response.HandoffReason,
            ExternalJobId = response.ExternalJobId,
            ExpectedBy = response.ExpectedBy,
            CloudEventId = response.CloudEventId,
            CloudEventSource = response.CloudEventSource,
            CloudEventType = response.CloudEventType,
            CloudEventSubject = response.CloudEventSubject,
        };
    }

    /// <summary>
    /// <see cref="UnresolvedEvent"/> declares these properties non-nullable, but null is what the
    /// Resolver actually writes for a clean response; the forgiveness operator states that fact
    /// once here rather than at every assignment.
    /// </summary>
    private static string NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null! : value;

    private static bool SameSession(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    private static bool IsFrom(MessageEntity message, string endpointId) =>
        string.Equals(message.From, endpointId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A message with no stamped <c>EndpointId</c> (pre-dating the field) stays in scope: it can
    /// only add blockers, never make a row repairable that would not otherwise be.
    /// </summary>
    private static bool ConcernsEndpoint(MessageEntity message, string endpointId) =>
        string.IsNullOrEmpty(message.EndpointId)
        || string.Equals(message.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase);
}
