using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Dispatches pending outbox messages to the real message sender.
    /// This is the core polling logic; wrap in a hosted service for background execution.
    /// </summary>
    public class OutboxDispatcher
    {
        private static readonly TimeSpan DefaultCompensatingCheckpointTimeout = TimeSpan.FromSeconds(5);
        private readonly IOutbox _outbox;
        private readonly ISender _sender;
        private readonly IOutboxDispatchCoordinator? _coordinator;
        private readonly ILogger<OutboxDispatcher> _logger;
        private readonly TimeSpan _compensatingCheckpointTimeout;

        public OutboxDispatcher(IOutbox outbox, ISender sender, ILogger<OutboxDispatcher>? logger = null)
            : this(outbox, sender, coordinator: null, DefaultCompensatingCheckpointTimeout, logger)
        {
        }

        /// <summary>
        /// Initializes an outbox dispatcher with a bounded timeout for the best-effort
        /// checkpoint performed after cooperative cancellation.
        /// </summary>
        /// <param name="outbox">The transactional outbox store.</param>
        /// <param name="sender">The sender used to dispatch stored messages.</param>
        /// <param name="compensatingCheckpointTimeout">Maximum time to wait for the cancellation checkpoint.</param>
        /// <param name="logger">Optional dispatcher logger.</param>
        public OutboxDispatcher(
            IOutbox outbox,
            ISender sender,
            TimeSpan compensatingCheckpointTimeout,
            ILogger<OutboxDispatcher>? logger = null)
            : this(outbox, sender, coordinator: null, compensatingCheckpointTimeout, logger)
        {
        }

        /// <summary>
        /// Initializes an outbox dispatcher with an optional due-time dispatch
        /// coordinator (spec 025). The claim/fence/checkpoint protocol runs iff
        /// <paramref name="coordinator"/> is non-null and reports
        /// <see cref="IOutboxDispatchCoordinator.DueTimeDispatchActive"/>; in every
        /// other case the legacy GetPendingAsync/MarkAsDispatchedAsync flow runs
        /// unchanged.
        /// </summary>
        /// <param name="outbox">The transactional outbox store.</param>
        /// <param name="sender">The sender used to dispatch stored messages.</param>
        /// <param name="coordinator">Optional due-time dispatch coordinator.</param>
        /// <param name="logger">Optional dispatcher logger.</param>
        public OutboxDispatcher(
            IOutbox outbox,
            ISender sender,
            IOutboxDispatchCoordinator? coordinator,
            ILogger<OutboxDispatcher>? logger = null)
            : this(outbox, sender, coordinator, DefaultCompensatingCheckpointTimeout, logger)
        {
        }

        /// <summary>
        /// Initializes an outbox dispatcher with an optional due-time dispatch
        /// coordinator and a bounded compensating-checkpoint timeout.
        /// </summary>
        /// <param name="outbox">The transactional outbox store.</param>
        /// <param name="sender">The sender used to dispatch stored messages.</param>
        /// <param name="coordinator">Optional due-time dispatch coordinator.</param>
        /// <param name="compensatingCheckpointTimeout">Maximum time to wait for the cancellation checkpoint.</param>
        /// <param name="logger">Optional dispatcher logger.</param>
        public OutboxDispatcher(
            IOutbox outbox,
            ISender sender,
            IOutboxDispatchCoordinator? coordinator,
            TimeSpan compensatingCheckpointTimeout,
            ILogger<OutboxDispatcher>? logger = null)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            if (compensatingCheckpointTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(compensatingCheckpointTimeout));

            _coordinator = coordinator;
            _compensatingCheckpointTimeout = compensatingCheckpointTimeout;
            _logger = logger ?? NullLogger<OutboxDispatcher>.Instance;
        }

        /// <summary>
        /// Dispatches a batch of pending outbox messages.
        /// </summary>
        /// <param name="batchSize">Maximum number of messages to dispatch in one batch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of messages dispatched.</returns>
        public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            // Protocol selection is pinned by the capability signal, never by
            // registration presence (spec 025): only an ACTIVE coordinator runs the
            // claim/fence/checkpoint protocol; default mode and custom providers
            // keep today's flow byte-identical.
            if (_coordinator is { DueTimeDispatchActive: true })
                return await DispatchViaCoordinatorAsync(_coordinator, batchSize, cancellationToken);

            var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
            if (pending.Count == 0)
                return 0;

            _logger.LogDebug("Outbox dispatch poll found {PendingCount} pending message(s)", pending.Count);

            var dispatched = new List<string>();
            // NimBus's ordering guarantee is FIFO per session (ADR-001), not global.
            // Halt only the failing session so one poison row cannot stall every
            // other session's outbound messages. Rows are oldest-first, so once a
            // session's row fails, later rows for that same session stay parked
            // behind it this poll and are retried next interval.
            var failedSessions = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var outboxMessage in pending)
                {
                    var sessionId = outboxMessage.SessionId;
                    var hasSession = !string.IsNullOrEmpty(sessionId);

                    if (hasSession && failedSessions.Contains(sessionId))
                    {
                        // A prior row on this session failed this poll; keep it strictly
                        // ordered behind its stuck row instead of dispatching out of order.
                        continue;
                    }

                    // Legacy flow: the send runs directly under the polling token, so
                    // the send token and the shutdown token are the same token.
                    if (!await DispatchOneAsync(outboxMessage, cancellationToken, cancellationToken))
                    {
                        // Block only this session; session-less rows fail independently.
                        // The failed message will be retried on the next poll.
                        if (hasSession)
                            failedSessions.Add(sessionId);
                        continue;
                    }

                    dispatched.Add(outboxMessage.Id);
                }

                if (dispatched.Count > 0)
                {
                    await _outbox.MarkAsDispatchedAsync(dispatched, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Sending and checkpointing are separate operations. Complete the
                // idempotent checkpoint without the canceled polling token, whether
                // cancellation interrupted sending or a partially-applied checkpoint.
                if (dispatched.Count > 0)
                {
                    Task? checkpointTask = null;
                    try
                    {
                        checkpointTask = _outbox.MarkAsDispatchedAsync(dispatched, CancellationToken.None);
                        await checkpointTask.WaitAsync(_compensatingCheckpointTimeout);
                    }
                    catch (Exception ex)
                    {
                        if (ex is TimeoutException && checkpointTask is not null)
                        {
                            _ = ObserveCompensatingCheckpointAsync(checkpointTask, dispatched.Count);
                        }

                        // The caller's cancellation remains the primary outcome. The
                        // pending rows may be sent again under the outbox's at-least-once
                        // delivery contract, but shutdown must not be reported as a
                        // dispatch failure because bookkeeping also became unavailable.
                        _logger.LogWarning(
                            ex,
                            "Could not checkpoint {DispatchedCount} outbox message(s) sent before cancellation",
                            dispatched.Count);
                    }
                }

                throw;
            }

            return dispatched.Count;
        }

        // ── SqlOwnedDueTime claim/fence/checkpoint protocol (spec 025) ──────

        private async Task<int> DispatchViaCoordinatorAsync(
            IOutboxDispatchCoordinator coordinator,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var claimId = Guid.NewGuid();
            var claimed = await coordinator.ClaimDueAsync(claimId, batchSize, cancellationToken);
            if (claimed.Count == 0)
                return 0;

            _logger.LogDebug(
                "Outbox due-time claim round {ClaimId} claimed {ClaimedCount} row(s)",
                claimId, claimed.Count);

            var dispatched = 0;
            for (var i = 0; i < claimed.Count; i++)
            {
                try
                {
                    if (await DispatchClaimedOneAsync(coordinator, claimed[i], claimId, cancellationToken))
                        dispatched++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown mid-batch: release the not-yet-started claims (best
                    // effort, no token) so another worker can pick them up immediately
                    // instead of waiting out the lease. Cleanup starts at the CURRENT
                    // item, not the next one: cancellation may have interrupted it
                    // before its dispatch-start fence won, leaving it reserved and its
                    // whole session blocked for SendLeaseDuration. ReleaseClaimAsync
                    // no-ops safely for a started, terminal, or stale claim, so
                    // including the current item can never un-reserve a live send.
                    for (var j = i; j < claimed.Count; j++)
                    {
                        try
                        {
                            await coordinator.ReleaseClaimAsync(claimed[j].Id, claimId, CancellationToken.None)
                                .WaitAsync(_compensatingCheckpointTimeout);
                        }
                        catch (Exception releaseEx)
                        {
                            _logger.LogWarning(
                                releaseEx,
                                "Could not release outbox claim for row {OutboxId} during shutdown; the lease will expire on its own",
                                claimed[j].Id);
                        }
                    }

                    throw;
                }
            }

            return dispatched;
        }

        private async Task<bool> DispatchClaimedOneAsync(
            IOutboxDispatchCoordinator coordinator,
            OutboxMessage outboxMessage,
            Guid claimId,
            CancellationToken cancellationToken)
        {
            // Clock-independent send budgeting (spec 025 rev 6): anchor a monotonic
            // timer BEFORE the fence call, so elapsed-since-call-start strictly
            // over-counts consumed lease time. The SQL-returned deadline is the
            // authoritative server-side reclaim boundary for other workers; it is
            // logged, never compared against the client clock.
            var window = coordinator.UsableSendWindow;
            var anchor = Stopwatch.GetTimestamp();
            var deadline = await coordinator.TryStartDispatchAsync(outboxMessage.Id, claimId, cancellationToken);
            if (deadline is null)
            {
                _logger.LogDebug(
                    "Outbox dispatch-start fence lost for row {OutboxId} (cancelled or ownership lost); skipping send",
                    outboxMessage.Id);
                return false;
            }

            var residual = window - Stopwatch.GetElapsedTime(anchor);
            if (residual < IOutboxDispatchCoordinator.MinimumUsableSendWindow)
            {
                // The fence round trip consumed the usable window. Re-fence ONCE for a
                // full fresh lease (owner-idempotent renewal) instead of starting a
                // send that instantly times out; a null re-fence means ownership was
                // lost to an expired-head reclaim.
                anchor = Stopwatch.GetTimestamp();
                deadline = await coordinator.TryStartDispatchAsync(outboxMessage.Id, claimId, cancellationToken);
                if (deadline is null)
                {
                    _logger.LogDebug(
                        "Outbox lease renewal lost for row {OutboxId} (ownership reclaimed); abandoning attempt without send",
                        outboxMessage.Id);
                    return false;
                }

                residual = window - Stopwatch.GetElapsedTime(anchor);
                if (residual <= TimeSpan.Zero)
                {
                    // A single fence round trip persistently exceeds the configured
                    // (>= floor) window: the database is unhealthy. Degrade to a retry
                    // next round — never to a false outcome.
                    _logger.LogWarning(
                        "Outbox fence round trip for row {OutboxId} exceeded the usable send window {Window}; retrying next round (SQL lease deadline {Deadline:O})",
                        outboxMessage.Id, window, deadline);
                    return false;
                }
            }

            _logger.LogDebug(
                "Outbox dispatch started for row {OutboxId} under claim {ClaimId}; send budget {Budget}, SQL lease deadline {Deadline:O}",
                outboxMessage.Id, claimId, residual, deadline);

            using var sendBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendBudget.CancelAfter(residual);
            bool sent;
            try
            {
                // A due scheduled row is sent immediately (Send, not ScheduleMessage):
                // SQL owned the due time until now, so there is no broker schedule and
                // no broker sequence to checkpoint or cancel.
                sent = await DispatchOneAsync(
                    outboxMessage, sendBudget.Token, cancellationToken, forceImmediateSend: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Bounded-send budget expired. The broker outcome is ambiguous, so the
                // row keeps DispatchStartedAtUtc (cancellation can no longer claim
                // prevention) and is retried after the lease expires; a duplicate
                // attempt carries the same MessageId and is absorbed by the
                // application idempotency guard.
                _logger.LogWarning(
                    "Outbox bounded send timed out for row {OutboxId} (budget {Budget}); the row remains its session's head and will be retried",
                    outboxMessage.Id, residual);
                return false;
            }

            if (!sent)
                return false;

            return await CheckpointClaimedAsync(coordinator, outboxMessage, claimId, cancellationToken);
        }

        private async Task<bool> CheckpointClaimedAsync(
            IOutboxDispatchCoordinator coordinator,
            OutboxMessage outboxMessage,
            Guid claimId,
            CancellationToken cancellationToken)
        {
            bool owned;
            try
            {
                owned = await coordinator.TryCompleteAsync(outboxMessage.Id, claimId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Sending and checkpointing are separate operations: complete the
                // owned checkpoint without the canceled polling token, bounded, so a
                // sent row is not needlessly replayed after shutdown.
                Task<bool>? checkpointTask = null;
                try
                {
                    checkpointTask = coordinator.TryCompleteAsync(outboxMessage.Id, claimId, CancellationToken.None);
                    await checkpointTask.WaitAsync(_compensatingCheckpointTimeout);
                }
                catch (Exception ex)
                {
                    if (ex is TimeoutException && checkpointTask is not null)
                    {
                        _ = ObserveCompensatingCheckpointAsync(checkpointTask, 1);
                    }

                    _logger.LogWarning(
                        ex,
                        "Could not checkpoint outbox row {OutboxId} sent before cancellation",
                        outboxMessage.Id);
                }

                throw;
            }

            if (!owned)
            {
                // Stale owner: another worker reclaimed the row after our lease
                // expired mid-send. Exactly one attempt terminalizes the row; this
                // one's checkpoint affected zero rows and the duplicate delivery is
                // absorbed by the application guard (invariant 9).
                _logger.LogWarning(
                    "Outbox checkpoint for row {OutboxId} affected zero rows (stale owner after lease expiry); the duplicate delivery is absorbed by the application idempotency guard",
                    outboxMessage.Id);
                return false;
            }

            return true;
        }

        private async Task ObserveCompensatingCheckpointAsync(Task checkpointTask, int dispatchedCount)
        {
            try
            {
                await checkpointTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Outbox checkpoint completed with an error after shutdown stopped waiting for {DispatchedCount} message(s)",
                    dispatchedCount);
            }
        }

        /// <param name="cancellationToken">
        /// The token the send runs under. In due-time mode this is the bounded
        /// send-budget token, which is LINKED to <paramref name="shutdownToken"/>.
        /// </param>
        /// <param name="shutdownToken">
        /// The polling loop's own token. Cancellation observed while this token is
        /// unset is a budget expiry — a failed dispatch attempt — not host shutdown.
        /// </param>
        private async Task<bool> DispatchOneAsync(
            OutboxMessage outboxMessage,
            CancellationToken cancellationToken,
            CancellationToken shutdownToken,
            bool forceImmediateSend = false)
        {
            // Detach from the polling-loop activity so the dispatch span is a root that
            // links (rather than nests under) the original publish context.
            var savedCurrent = Activity.Current;
            Activity.Current = null;

            var startTimestamp = Stopwatch.GetTimestamp();
            Message message = null;
            // Endpoint comes from the persisted column so dispatch metrics are
            // tagged even when payload deserialization fails on a malformed row.
            var endpoint = outboxMessage.To;
            Activity activity = null;

            try
            {
                ActivityLink? link = TryBuildLink(outboxMessage.TraceParent, outboxMessage.TraceState);
                // FR-015: messaging spans use "{operation.type} {destination.name}".
                var spanName = string.IsNullOrEmpty(endpoint) ? "publish" : $"publish {endpoint}";
                activity = NimBusActivitySources.Outbox.StartActivity(
                    spanName,
                    ActivityKind.Producer,
                    parentContext: default,
                    tags: null,
                    links: link.HasValue ? new[] { link.Value } : null);

                if (activity is not null)
                {
                    if (!link.HasValue)
                        activity.AddEvent(new ActivityEvent("nimbus.outbox.orphan_row"));
                    // FR-020: messaging spans MUST set messaging.system /
                    // messaging.operation.type / messaging.destination.name.
                    // System is unknown at this layer (NimBus.Core is transport-
                    // agnostic) — set what we know; transport wrappers may add it.
                    activity.SetTag(MessagingAttributes.OperationType, "publish");
                    if (!string.IsNullOrEmpty(endpoint))
                    {
                        activity.SetTag(MessagingAttributes.DestinationName, endpoint);
                        activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
                    }
                    if (!string.IsNullOrEmpty(outboxMessage.EventTypeId))
                        activity.SetTag(MessagingAttributes.NimBusEventType, outboxMessage.EventTypeId);
                    if (!string.IsNullOrEmpty(outboxMessage.MessageId))
                        activity.SetTag(MessagingAttributes.MessageId, outboxMessage.MessageId);
                    if (!string.IsNullOrEmpty(outboxMessage.CorrelationId))
                        activity.SetTag(MessagingAttributes.MessageConversationId, outboxMessage.CorrelationId);
                    if (outboxMessage.ScheduledEnqueueTimeUtc.HasValue)
                    {
                        // A due scheduled row is not an ordinary publish: say so on the
                        // span, with the bounded mode that decided when it fired.
                        activity.SetTag(MessagingAttributes.NimBusScheduleOperation, "dispatch");
                        activity.SetTag(
                            MessagingAttributes.NimBusScheduleMode,
                            forceImmediateSend ? "sql_outbox" : "broker");
                    }
                }

                message = JsonConvert.DeserializeObject<Message>(
                    outboxMessage.Payload,
                    Constants.CreateSafeJsonSettings());

                if (outboxMessage.ScheduledEnqueueTimeUtc.HasValue && !forceImmediateSend)
                {
                    await _sender.ScheduleMessage(message, new DateTimeOffset(outboxMessage.ScheduledEnqueueTimeUtc.Value, TimeSpan.Zero), cancellationToken);
                }
                else
                {
                    // forceImmediateSend (SqlOwnedDueTime): a due scheduled row is sent
                    // with zero delay rather than eagerly broker-scheduled. Unscheduled
                    // rows keep their legacy broker-side enqueue delay in both modes.
                    var delayMinutes = forceImmediateSend && outboxMessage.ScheduledEnqueueTimeUtc.HasValue
                        ? 0
                        : outboxMessage.EnqueueDelayMinutes;
                    await _sender.Send(message, delayMinutes, cancellationToken);
                }

                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = BuildDispatchTags(endpoint, "dispatched", errorType: null);
                NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
                NimBusMeters.OutboxDispatched.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag(MessagingAttributes.NimBusOutcome, "dispatched");

                if (outboxMessage.ScheduledEnqueueTimeUtc.HasValue)
                {
                    // Distinguishable in logs, not just on the span: a scheduled row
                    // reaching the broker is the "dispatched" milestone of a timeout.
                    _logger.LogInformation(
                        "Outbox dispatched scheduled message {OutboxId} due {DueAtUtc:O} (mode {ScheduleMode}, event {EventTypeId}, session {SessionId}, messageId {MessageId})",
                        outboxMessage.Id,
                        outboxMessage.ScheduledEnqueueTimeUtc.Value,
                        forceImmediateSend ? "sql_outbox" : "broker",
                        outboxMessage.EventTypeId,
                        outboxMessage.SessionId,
                        outboxMessage.MessageId);
                }
                else
                {
                    _logger.LogDebug(
                        "Outbox dispatched message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId})",
                        outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                }

                return true;
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // A stopped polling loop is not a failed outbox dispatch. Propagate
                // cancellation without failure metrics/logging so the row remains
                // pending for the next active dispatcher.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Not shutdown: the bounded send budget expired (the linked token
                // fired on its own). That IS a failed dispatch attempt and must be
                // recorded as one (AC6) — here, while the dispatch activity is still
                // live. Rethrow so the caller applies its lease-specific compensation.
                RecordDispatchFailure(activity, endpoint, startTimestamp, ex);
                activity?.AddEvent(new ActivityEvent("nimbus.outbox.send_budget_expired"));
                _logger.LogWarning(
                    ex,
                    "Outbox dispatch exceeded its send budget for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId})",
                    outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                throw;
            }
            catch (Exception ex)
            {
                RecordDispatchFailure(activity, endpoint, startTimestamp, ex);
                _logger.LogError(
                    ex,
                    "Outbox dispatch failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}). Halting this poll; will retry next interval.",
                    outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                return false;
            }
            finally
            {
                activity?.Dispose();
                Activity.Current = savedCurrent;
            }
        }

        /// <summary>
        /// Stamps the "failed" dispatch metric and marks the (still live) dispatch
        /// span as errored. Shared by the budget-expiry and ordinary-failure paths so
        /// both are visible identically to an operator.
        /// </summary>
        private static void RecordDispatchFailure(Activity activity, string endpoint, long startTimestamp, Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var errorType = ex.GetType().FullName;
            var tags = BuildDispatchTags(endpoint, "failed", errorType);
            NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
            NimBusMeters.OutboxDispatched.Add(1, tags);
            if (activity is null)
                return;

            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.SetTag(MessagingAttributes.NimBusOutcome, "failed");
            activity.SetTag(MessagingAttributes.ErrorType, errorType);
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", errorType },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));
        }

        private static ActivityLink? TryBuildLink(string traceParent, string traceState)
        {
            var context = W3CMessagePropagator.TryParse(traceParent, traceState);
            return context == default ? (ActivityLink?)null : new ActivityLink(context);
        }

        private static KeyValuePair<string, object?>[] BuildDispatchTags(string? endpoint, string outcome, string? errorType)
        {
            var tags = new List<KeyValuePair<string, object?>>(3)
            {
                new(MessagingAttributes.NimBusOutcome, outcome),
            };
            if (!string.IsNullOrEmpty(endpoint))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
            if (!string.IsNullOrEmpty(errorType))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
            return tags.ToArray();
        }
    }
}
