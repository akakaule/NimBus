using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Outermost pipeline behavior. Owns the consumer-side <c>NimBus.Process</c>
/// activity and emits the consumer counters and histograms (via
/// <see cref="NimBusMeters.Consumer"/>). The span lifetime covers the entire
/// pipeline run, including broker-settle calls that happen inside
/// <see cref="IMessageContext.Complete"/> / <see cref="IMessageContext.DeadLetter"/> /
/// <see cref="IMessageContext.Defer"/>, so this middleware MUST be registered
/// first (outermost) — registering anything before it truncates the span.
/// </summary>
public sealed class MetricsMiddleware : IMessagePipelineBehavior
{
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        var destination = context.To ?? "unknown";
        var eventType = context.EventTypeId ?? "unknown";

        using var activity = NimBusActivitySources.Consumer.StartActivity(
            "process " + destination,
            ActivityKind.Consumer,
            context.ParentTraceContext);

        var hasParent = context.ParentTraceContext != default;

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.OperationType, "process");
            activity.SetTag(MessagingAttributes.DestinationName, destination);
            activity.SetTag(MessagingAttributes.NimBusEventType, eventType);
            if (!string.IsNullOrEmpty(context.MessageId))
                activity.SetTag(MessagingAttributes.MessageId, context.MessageId);
            if (!string.IsNullOrEmpty(context.CorrelationId))
                activity.SetTag(MessagingAttributes.MessageConversationId, context.CorrelationId);
            if (!string.IsNullOrEmpty(context.SessionId))
                activity.SetTag(MessagingAttributes.NimBusSessionKey, context.SessionId);
            activity.SetTag(MessagingAttributes.NimBusHasParentTrace, hasParent);
        }

        var receivedTags = new TagList
        {
            { MessagingAttributes.DestinationName, destination },
            { MessagingAttributes.NimBusEventType, eventType },
        };
        NimBusMeters.MessagesReceived.Add(1, receivedTags);

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            RecordOutcome(activity, receivedTags, sw, outcome: "completed");
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordOutcome(activity, receivedTags, sw, outcome: "failed", ex);
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;
            throw;
        }
    }

    private static void RecordOutcome(Activity? activity, TagList baseTags, Stopwatch sw, string outcome, Exception? ex = null)
    {
        var processedTags = baseTags;
        processedTags.Add(MessagingAttributes.NimBusOutcome, outcome);

        NimBusMeters.MessagesProcessed.Add(1, processedTags);
        NimBusMeters.ProcessDuration.Record(sw.Elapsed.TotalMilliseconds, processedTags);

        if (ex is not null && activity is { IsAllDataRequested: true })
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
}
