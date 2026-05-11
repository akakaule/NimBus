#pragma warning disable CA1707, CA2007, CS8601
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public sealed class OutboxSenderInstrumentationTests
{
    [TestMethod]
    public async Task Send_emits_enqueue_span_and_captures_trace_context()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource("nimbus.test.outbox-enqueue")
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        using var source = new ActivitySource("nimbus.test.outbox-enqueue");
        using var parent = source.StartActivity("publisher", ActivityKind.Producer);
        Assert.IsNotNull(parent);
        parent.TraceStateString = "vendor=outbox";

        var outbox = new InMemoryRecordingOutbox();
        var sender = new OutboxSender(outbox);

        await sender.Send(CreateMessage("msg-1", "OrderPlaced", "session-1"));
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        Assert.AreEqual(1, outbox.Stored.Count);
        var stored = outbox.Stored[0];
        Assert.AreEqual(parent.Id, stored.TraceParent);
        Assert.AreEqual("vendor=outbox", stored.TraceState);

        var enqueueSpan = activities.Single(a =>
            a.Source.Name == NimBusInstrumentation.OutboxActivitySourceName &&
            a.OperationName == "NimBus.Outbox.Enqueue");
        Assert.AreEqual(ActivityKind.Internal, enqueueSpan.Kind);

        var enqueued = metrics.Single(m => m.Name == "nimbus.outbox.enqueued");
        Assert.AreEqual(1, SumLong(enqueued));
        Assert.IsTrue(MetricTagKeys(enqueued).Contains(MessagingAttributes.NimBusEventType));
    }

    [TestMethod]
    public async Task Send_batch_records_one_count_per_event_type()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var outbox = new InMemoryRecordingOutbox();
        var sender = new OutboxSender(outbox);

        await sender.Send(new[]
        {
            CreateMessage("msg-1", "OrderPlaced", "s-1"),
            CreateMessage("msg-2", "OrderPlaced", "s-1"),
            CreateMessage("msg-3", "PaymentCaptured", "s-2"),
        });
        meterProvider.ForceFlush();

        var enqueued = metrics.Single(m => m.Name == "nimbus.outbox.enqueued");
        Assert.AreEqual(3, SumLong(enqueued));
    }

    [TestMethod]
    public async Task Send_without_current_activity_persists_null_trace_context()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var outbox = new InMemoryRecordingOutbox();
            var sender = new OutboxSender(outbox);

            await sender.Send(CreateMessage("msg-1", "OrderPlaced", "s-1"));

            Assert.IsNull(outbox.Stored[0].TraceParent);
            Assert.IsNull(outbox.Stored[0].TraceState);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private static Message CreateMessage(string messageId, string eventTypeId, string sessionId) => new()
    {
        MessageId = messageId,
        EventTypeId = eventTypeId,
        SessionId = sessionId,
        CorrelationId = "corr-1",
        To = "endpoint-1",
        EventId = "evt-1",
        MessageType = MessageType.EventRequest,
        OriginatingMessageId = "self",
        ParentMessageId = "self",
        From = "publisher",
        OriginatingFrom = "publisher",
        OriginalSessionId = sessionId,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" }
        }
    };

    internal static long SumLong(Metric metric)
    {
        long sum = 0;
        foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            sum += metricPoint.GetSumLong();
        return sum;
    }

    internal static IEnumerable<string> MetricTagKeys(Metric metric)
    {
        foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            foreach (var tag in metricPoint.Tags)
                yield return tag.Key;
    }
}

[TestClass]
public sealed class OutboxDispatcherInstrumentationTests
{
    [TestMethod]
    public async Task Dispatch_links_original_traceparent_via_ActivityLink()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{traceId.ToHexString()}-{spanId.ToHexString()}-01";

        var outbox = new InMemoryRecordingOutbox();
        outbox.AddPending(BuildOutboxRow("out-1", traceParent: traceParent, traceState: "vendor=trace"));

        var sender = new RecordingDispatchSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var dispatched = await dispatcher.DispatchPendingAsync();
        tracer.ForceFlush();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(1, sender.Sent.Count);

        var dispatchSpan = activities.Single(a =>
            a.Source.Name == NimBusInstrumentation.OutboxActivitySourceName &&
            a.OperationName == "publish endpoint-1");
        Assert.AreEqual(ActivityKind.Producer, dispatchSpan.Kind);
        Assert.AreEqual(default, dispatchSpan.ParentSpanId, "Dispatch span must be a root span");

        var link = dispatchSpan.Links.Single();
        Assert.AreEqual(traceId, link.Context.TraceId);
        Assert.AreEqual(spanId, link.Context.SpanId);
        Assert.AreEqual("vendor=trace", link.Context.TraceState);
        Assert.IsFalse(dispatchSpan.Events.Any(e => e.Name == "nimbus.outbox.orphan_row"));
        Assert.AreEqual(ActivityStatusCode.Ok, dispatchSpan.Status);
    }

    [TestMethod]
    public async Task Dispatch_span_carries_messaging_attributes()
    {
        // Covers the P1.4 fix from the PR #42 review: messaging.operation.type,
        // messaging.destination.name, messaging.message.id, and
        // messaging.message.conversation_id must be set on the dispatch span.
        // messaging.system is intentionally not asserted — the dispatcher is
        // transport-agnostic and the spec gap is tracked in phase-4.2-plan.md.
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var outbox = new InMemoryRecordingOutbox();
        outbox.AddPending(BuildOutboxRow("out-1", traceParent: null, traceState: null));

        var sender = new RecordingDispatchSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        await dispatcher.DispatchPendingAsync();
        tracer.ForceFlush();

        var dispatchSpan = activities.Single(a =>
            a.Source.Name == NimBusInstrumentation.OutboxActivitySourceName &&
            a.OperationName == "publish endpoint-1");

        Assert.AreEqual("publish", dispatchSpan.GetTagItem(MessagingAttributes.OperationType));
        Assert.AreEqual("endpoint-1", dispatchSpan.GetTagItem(MessagingAttributes.DestinationName));
        Assert.AreEqual("msg-out-1", dispatchSpan.GetTagItem(MessagingAttributes.MessageId));
        Assert.AreEqual("corr-1", dispatchSpan.GetTagItem(MessagingAttributes.MessageConversationId));
    }

    [TestMethod]
    public async Task Dispatch_orphan_row_emits_event_and_succeeds()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var outbox = new InMemoryRecordingOutbox();
        outbox.AddPending(BuildOutboxRow("out-1", traceParent: null, traceState: null));

        var sender = new RecordingDispatchSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var dispatched = await dispatcher.DispatchPendingAsync();
        tracer.ForceFlush();

        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(1, sender.Sent.Count);

        var dispatchSpan = activities.Single(a =>
            a.Source.Name == NimBusInstrumentation.OutboxActivitySourceName &&
            a.OperationName == "publish endpoint-1");
        Assert.AreEqual(0, dispatchSpan.Links.Count());
        Assert.IsTrue(dispatchSpan.Events.Any(e => e.Name == "nimbus.outbox.orphan_row"));
    }

    [TestMethod]
    public async Task Dispatch_failure_records_error_and_increments_failed_outcome()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var outbox = new InMemoryRecordingOutbox();
        outbox.AddPending(BuildOutboxRow("out-1", traceParent: null, traceState: null));

        var sender = new ThrowingSender(new InvalidOperationException("send blew up"));
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var dispatched = await dispatcher.DispatchPendingAsync();
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, outbox.MarkedDispatched.Count, "Failed row must not be marked dispatched");

        var dispatchSpan = activities.Single(a =>
            a.Source.Name == NimBusInstrumentation.OutboxActivitySourceName &&
            a.OperationName == "publish endpoint-1");
        Assert.AreEqual(ActivityStatusCode.Error, dispatchSpan.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, dispatchSpan.GetTagItem(MessagingAttributes.ErrorType));
        Assert.IsTrue(dispatchSpan.Events.Any(e => e.Name == "exception"));

        var dispatchedCounter = metrics.Single(m => m.Name == "nimbus.outbox.dispatched");
        var (failedSum, failedTags) = SumByOutcome(dispatchedCounter, "failed");
        Assert.AreEqual(1, failedSum);
        Assert.IsTrue(failedTags.ContainsKey(MessagingAttributes.ErrorType));
        Assert.AreEqual(typeof(InvalidOperationException).FullName, failedTags[MessagingAttributes.ErrorType]);
    }

    [TestMethod]
    public async Task Dispatch_records_dispatched_outcome_with_endpoint_tag()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var outbox = new InMemoryRecordingOutbox();
        outbox.AddPending(BuildOutboxRow("out-1", traceParent: null, traceState: null));

        var sender = new RecordingDispatchSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        await dispatcher.DispatchPendingAsync();
        meterProvider.ForceFlush();

        var dispatchedCounter = metrics.Single(m => m.Name == "nimbus.outbox.dispatched");
        var (sum, tags) = SumByOutcome(dispatchedCounter, "dispatched");
        Assert.AreEqual(1, sum);
        Assert.AreEqual("endpoint-1", tags[MessagingAttributes.NimBusEndpoint]);

        var duration = metrics.Single(m => m.Name == "nimbus.outbox.dispatch.duration");
        Assert.IsTrue(HistogramCount(duration) >= 1);
    }

    private static OutboxMessage BuildOutboxRow(string id, string? traceParent, string? traceState) => new()
    {
        Id = id,
        MessageId = $"msg-{id}",
        To = "endpoint-1",
        EventTypeId = "OrderPlaced",
        SessionId = "session-1",
        CorrelationId = "corr-1",
        EnqueueDelayMinutes = 0,
        CreatedAtUtc = DateTime.UtcNow,
        TraceParent = traceParent,
        TraceState = traceState,
        Payload = JsonConvert.SerializeObject(new Message
        {
            MessageId = $"msg-{id}",
            EventTypeId = "OrderPlaced",
            SessionId = "session-1",
            CorrelationId = "corr-1",
            To = "endpoint-1",
            EventId = "evt-1",
            MessageType = MessageType.EventRequest,
            OriginatingMessageId = "self",
            ParentMessageId = "self",
            From = "publisher",
            OriginatingFrom = "publisher",
            OriginalSessionId = "session-1",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" }
            }
        })
    };

    private static (long Sum, IReadOnlyDictionary<string, object?> Tags) SumByOutcome(Metric metric, string outcome)
    {
        long sum = 0;
        Dictionary<string, object?>? matched = null;
        foreach (ref readonly var metricPoint in metric.GetMetricPoints())
        {
            var tags = new Dictionary<string, object?>();
            foreach (var tag in metricPoint.Tags)
                tags[tag.Key] = tag.Value;
            if (tags.TryGetValue(MessagingAttributes.NimBusOutcome, out var v) &&
                string.Equals(v?.ToString(), outcome, StringComparison.Ordinal))
            {
                sum += metricPoint.GetSumLong();
                matched = tags;
            }
        }
        return (sum, (IReadOnlyDictionary<string, object?>)(matched ?? new Dictionary<string, object?>()));
    }

    private static long HistogramCount(Metric metric)
    {
        long count = 0;
        foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            count += metricPoint.GetHistogramCount();
        return count;
    }
}

internal sealed class InMemoryRecordingOutbox : IOutbox
{
    public List<OutboxMessage> Stored { get; } = new();
    public List<OutboxMessage> BatchStored { get; } = new();
    public List<string> MarkedDispatched { get; } = new();

    private readonly List<OutboxMessage> _pending = new();

    public void AddPending(OutboxMessage message) => _pending.Add(message);

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        Stored.Add(message);
        return Task.CompletedTask;
    }

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        BatchStored.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Take(batchSize).ToList());

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        MarkedDispatched.Add(id);
        return Task.CompletedTask;
    }

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        MarkedDispatched.AddRange(ids);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingDispatchSender : ISender
{
    public List<IMessage> Sent { get; } = new();
    public List<(IMessage Message, DateTimeOffset At)> Scheduled { get; } = new();

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        Sent.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        Scheduled.Add((message, scheduledEnqueueTime));
        return Task.FromResult(7L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class ThrowingSender : ISender
{
    private readonly Exception _exception;

    public ThrowingSender(Exception exception) => _exception = exception;

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) => throw _exception;
    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) => throw _exception;
    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) => throw _exception;
    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
