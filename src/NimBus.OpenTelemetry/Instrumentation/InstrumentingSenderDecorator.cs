using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;

namespace NimBus.OpenTelemetry.Instrumentation;

/// <summary>
/// Wraps an inner <see cref="ISender"/> and emits the publisher span
/// (<c>publish {destination}</c>) plus the publish counters and histograms.
/// Registered automatically by <c>AddNimBusInstrumentation</c>.
/// </summary>
internal sealed class InstrumentingSenderDecorator : ISender
{
    private readonly ISender _inner;
    private readonly string _messagingSystem;
    private readonly ILogger _logger;

    public InstrumentingSenderDecorator(ISender inner, string messagingSystem, ILogger logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _messagingSystem = messagingSystem ?? throw new ArgumentNullException(nameof(messagingSystem));
        _logger = logger;
    }

    /// <summary>
    /// The schedule mode to report when no handle exists yet (a failed schedule).
    /// The outbox sender always mints SQL-outbox handles; everything else is
    /// broker-backed. Decorator order is instrumenting → outbox → transport, so
    /// the inner sender is the authority.
    /// </summary>
    private string InnerScheduleMode => _inner is OutboxSender ? "sql_outbox" : "broker";

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendInstrumented([message], () => _inner.Send(message, messageEnqueueDelay, cancellationToken));
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var snapshot = messages as IReadOnlyCollection<IMessage> ?? messages.ToList();
        return SendInstrumented(snapshot, () => _inner.Send(snapshot, messageEnqueueDelay, cancellationToken));
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendInstrumentedAsync([message], () => _inner.ScheduleMessage(message, scheduledEnqueueTime, cancellationToken));
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
        => _inner.CancelScheduledMessage(sequenceNumber, cancellationToken);

    // The richer handle overloads MUST forward explicitly: without these the
    // decorator would satisfy the interface through the default bridge and hide
    // the inner sender's implementation (e.g. OutboxSender's provider-local
    // handle path), silently downgrading outbox scheduling to broker semantics.
    public Task<ScheduledMessageHandle> ScheduleMessageWithHandle(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ScheduleWithHandleInstrumentedAsync(message, () => _inner.ScheduleMessageWithHandle(message, scheduledEnqueueTime, cancellationToken));
    }

    public async Task<ScheduledMessageCancellationOutcome> CancelScheduledMessage(ScheduledMessageHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var mode = ScheduleMode(handle.Kind);

        // Cancellation is a settle-shaped operation with no message and no
        // destination, so it gets its OWN span rather than riding a publish span:
        // without one, a cancel is invisible in a trace.
        using var activity = NimBusActivitySources.Publisher.StartActivity("cancel_scheduled", ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.System, _messagingSystem);
            activity.SetTag(MessagingAttributes.OperationType, "settle");
            activity.SetTag(MessagingAttributes.NimBusScheduleOperation, "cancel");
            activity.SetTag(MessagingAttributes.NimBusScheduleMode, mode);
            activity.SetTag(MessagingAttributes.NimBusScheduledMessageId, handle.TimeoutId);
        }

        try
        {
            var outcome = await _inner.CancelScheduledMessage(handle, cancellationToken).ConfigureAwait(false);
            var outcomeTag = CancelOutcomeTag(outcome);
            RecordScheduleOperation("cancel", mode, outcomeTag);
            activity?.SetTag(MessagingAttributes.NimBusOutcome, outcomeTag);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger?.LogInformation(
                "Cancelled scheduled message {ScheduledMessageId} (mode {ScheduleMode}) with outcome {CancelOutcome}",
                handle.TimeoutId, mode, outcomeTag);
            return outcome;
        }
        catch (Exception ex)
        {
            RecordScheduleOperation("cancel", mode, "failed");
            if (activity is { IsAllDataRequested: true })
            {
                activity.SetTag(MessagingAttributes.NimBusOutcome, "failed");
                activity.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            _logger?.LogWarning(
                ex,
                "Cancelling scheduled message {ScheduledMessageId} (mode {ScheduleMode}) failed; durable workflow state remains the final authority",
                handle.TimeoutId, mode);
            throw;
        }
    }

    private async Task<ScheduledMessageHandle> ScheduleWithHandleInstrumentedAsync(
        IMessage message,
        Func<Task<ScheduledMessageHandle>> action)
    {
        // A schedule gets its own span NAME ("schedule {destination}") and bounded
        // schedule attributes, so it is distinguishable from an ordinary publish in
        // a trace; messaging.operation.type stays "publish" because a schedule IS a
        // publish under the semantic conventions' enumerated values, and the publish
        // counters keep their existing dimensions (the schedule-operations counter is
        // recorded IN ADDITION, never double-counting).
        var timeoutId = message.ScheduledMessageId ?? message.MessageId;
        var (activity, started, tags) = StartActivity([message], spanOperation: "schedule");
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.NimBusScheduleOperation, "schedule");
            activity.SetTag(MessagingAttributes.NimBusScheduleMode, InnerScheduleMode);
            if (!string.IsNullOrEmpty(timeoutId))
                activity.SetTag(MessagingAttributes.NimBusScheduledMessageId, timeoutId);
        }

        try
        {
            var result = await action().ConfigureAwait(false);
            var mode = ScheduleMode(result.Kind);
            RecordSuccess(activity, [message], started, tags);
            RecordScheduleOperation("schedule", mode, "scheduled");
            if (activity is { IsAllDataRequested: true })
            {
                // The handle is the authority once it exists.
                activity.SetTag(MessagingAttributes.NimBusScheduleMode, mode);
                activity.SetTag(MessagingAttributes.NimBusOutcome, "scheduled");
            }

            _logger?.LogInformation(
                "Scheduled message {ScheduledMessageId} for {Destination} (mode {ScheduleMode}, sequence {SequenceNumber})",
                result.TimeoutId, message.To, mode, result.SequenceNumber);
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure(activity, started, tags, ex);
            // The mode comes from the inner sender: a failure has no handle, and
            // dropping the dimension would leave failures uncomparable to successes.
            RecordScheduleOperation("schedule", InnerScheduleMode, "failed");
            activity?.SetTag(MessagingAttributes.NimBusOutcome, "failed");
            _logger?.LogWarning(
                ex,
                "Scheduling message {ScheduledMessageId} for {Destination} (mode {ScheduleMode}) failed",
                timeoutId, message.To, InnerScheduleMode);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private static void RecordScheduleOperation(string operation, string? mode, string outcome)
    {
        var tags = new TagList
        {
            { MessagingAttributes.NimBusScheduleOperation, operation },
            { MessagingAttributes.NimBusOutcome, outcome },
        };
        if (mode is not null)
            tags.Add(MessagingAttributes.NimBusScheduleMode, mode);
        NimBusMeters.ScheduleOperations.Add(1, tags);
    }

    private static string ScheduleMode(ScheduledMessageHandleKind kind) =>
        kind == ScheduledMessageHandleKind.SqlOutboxSequenceNumber ? "sql_outbox" : "broker";

    private static string CancelOutcomeTag(ScheduledMessageCancellationOutcome outcome) => outcome switch
    {
        ScheduledMessageCancellationOutcome.CancellationRequested => "cancellation_requested",
        ScheduledMessageCancellationOutcome.CancelledBeforeDispatch => "cancelled_before_dispatch",
        ScheduledMessageCancellationOutcome.AlreadyCancelled => "already_cancelled",
        ScheduledMessageCancellationOutcome.TooLate => "too_late",
        ScheduledMessageCancellationOutcome.NotFound => "not_found",
        ScheduledMessageCancellationOutcome.Unsupported => "unsupported",
        _ => "failed",
    };

    private async Task SendInstrumented(IReadOnlyCollection<IMessage> messages, Func<Task> action)
    {
        var (activity, started, tags) = StartActivity(messages);
        try
        {
            await action().ConfigureAwait(false);
            RecordSuccess(activity, messages, started, tags);
        }
        catch (Exception ex)
        {
            RecordFailure(activity, started, tags, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async Task<long> SendInstrumentedAsync(IReadOnlyCollection<IMessage> messages, Func<Task<long>> action)
    {
        var (activity, started, tags) = StartActivity(messages);
        try
        {
            var result = await action().ConfigureAwait(false);
            RecordSuccess(activity, messages, started, tags);
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure(activity, started, tags, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private (Activity? activity, long startedAt, TagList tags) StartActivity(
        IReadOnlyCollection<IMessage> messages,
        string spanOperation = "publish")
    {
        var first = messages.FirstOrDefault();
        var destination = first?.To ?? "unknown";
        var eventType = first?.EventTypeId ?? "unknown";

        var spanName = spanOperation + " " + destination;
        var activity = NimBusActivitySources.Publisher.StartActivity(spanName, ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.System, _messagingSystem);
            activity.SetTag(MessagingAttributes.OperationType, "publish");
            activity.SetTag(MessagingAttributes.DestinationName, destination);
            activity.SetTag(MessagingAttributes.NimBusEventType, eventType);
            if (!string.IsNullOrEmpty(first?.MessageId))
                activity.SetTag(MessagingAttributes.MessageId, first.MessageId);
            if (!string.IsNullOrEmpty(first?.CorrelationId))
                activity.SetTag(MessagingAttributes.MessageConversationId, first.CorrelationId);
            if (!string.IsNullOrEmpty(first?.SessionId))
                activity.SetTag(MessagingAttributes.NimBusSessionKey, first.SessionId);
        }

        var tags = new TagList
        {
            { MessagingAttributes.System, _messagingSystem },
            { MessagingAttributes.DestinationName, destination },
            { MessagingAttributes.NimBusEventType, eventType },
        };

        return (activity, Stopwatch.GetTimestamp(), tags);
    }

    private static void RecordSuccess(Activity? activity, IReadOnlyCollection<IMessage> messages, long startedAt, TagList tags)
    {
        var elapsedMs = GetElapsedMs(startedAt);
        NimBusMeters.MessagesPublished.Add(messages.Count, tags);
        NimBusMeters.PublishDuration.Record(elapsedMs, tags);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private static void RecordFailure(Activity? activity, long startedAt, TagList tags, Exception ex)
    {
        var failureTags = tags;
        failureTags.Add(MessagingAttributes.ErrorType, ex.GetType().FullName ?? ex.GetType().Name);

        NimBusMeters.PublishFailed.Add(1, failureTags);
        NimBusMeters.PublishDuration.Record(GetElapsedMs(startedAt), failureTags);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.AddEvent(new ActivityEvent("exception", default, new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));
        }
    }

    private static double GetElapsedMs(long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return elapsed.TotalMilliseconds;
    }
}
