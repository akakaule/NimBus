using System.Diagnostics;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;

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

    public InstrumentingSenderDecorator(ISender inner, string messagingSystem)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _messagingSystem = messagingSystem ?? throw new ArgumentNullException(nameof(messagingSystem));
    }

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
        try
        {
            var outcome = await _inner.CancelScheduledMessage(handle, cancellationToken).ConfigureAwait(false);
            RecordScheduleOperation("cancel", ScheduleMode(handle.Kind), CancelOutcomeTag(outcome));
            return outcome;
        }
        catch (Exception)
        {
            RecordScheduleOperation("cancel", ScheduleMode(handle.Kind), "failed");
            throw;
        }
    }

    private async Task<ScheduledMessageHandle> ScheduleWithHandleInstrumentedAsync(
        IMessage message,
        Func<Task<ScheduledMessageHandle>> action)
    {
        // The publisher span + publish counters come from StartActivity/Record*
        // like ordinary sends; the bounded schedule-operations counter is
        // recorded IN ADDITION, never double-counting publish metrics.
        var (activity, started, tags) = StartActivity([message]);
        try
        {
            var result = await action().ConfigureAwait(false);
            RecordSuccess(activity, [message], started, tags);
            RecordScheduleOperation("schedule", ScheduleMode(result.Kind), "scheduled");
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure(activity, started, tags, ex);
            RecordScheduleOperation("schedule", mode: null, "failed");
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

    private (Activity? activity, long startedAt, TagList tags) StartActivity(IReadOnlyCollection<IMessage> messages)
    {
        var first = messages.FirstOrDefault();
        var destination = first?.To ?? "unknown";
        var eventType = first?.EventTypeId ?? "unknown";

        var spanName = "publish " + destination;
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
