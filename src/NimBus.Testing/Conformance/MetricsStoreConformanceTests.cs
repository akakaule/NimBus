using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IMetricsStore"/>.
/// Metrics are derived from stored messages and failed resolver state, so this
/// suite uses the aggregate <see cref="INimBusMessageStore"/> for setup.
/// </summary>
[TestClass]
public abstract class MetricsStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract INimBusMessageStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task GetEndpointMetrics_counts_message_types_since_from()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-published"), Id("msg-published"), MessageType.EventRequest, from.AddMinutes(1), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-handled"), Id("msg-handled"), MessageType.ResolutionResponse, from.AddMinutes(2), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-failed"), Id("msg-failed"), MessageType.ErrorResponse, from.AddMinutes(3), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-old"), Id("msg-old"), MessageType.EventRequest, from.AddMinutes(-30), endpointId: receiver, fromAddress: publisher));

        var metrics = await store.GetEndpointMetrics(from);

        Assert.AreEqual(1, metrics.Published.Single(m => m.EndpointId == publisher && m.EventTypeId == "OrderPlaced").Count);
        Assert.AreEqual(1, metrics.Handled.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced").Count);
        Assert.AreEqual(1, metrics.Failed.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced").Count);
    }

    [TestMethod]
    public async Task GetEndpointLatencyMetrics_aggregates_outcome_timings_since_from()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var receiver = Id("receiver");

        await store.StoreMessage(SampleMessage(Id("evt-lat-1"), Id("msg-lat-1"), MessageType.ResolutionResponse, from.AddMinutes(1), endpointId: receiver, queueTimeMs: 10, processingTimeMs: 100));
        await store.StoreMessage(SampleMessage(Id("evt-lat-2"), Id("msg-lat-2"), MessageType.ErrorResponse, from.AddMinutes(2), endpointId: receiver, queueTimeMs: 30, processingTimeMs: 300));
        await store.StoreMessage(SampleMessage(Id("evt-lat-old"), Id("msg-lat-old"), MessageType.ResolutionResponse, from.AddMinutes(-10), endpointId: receiver, queueTimeMs: 1000, processingTimeMs: 1000));

        var metrics = await store.GetEndpointLatencyMetrics(from);
        var row = metrics.Latencies.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced");

        Assert.AreEqual(2, row.Queue.Count);
        Assert.AreEqual(20, row.Queue.AvgMs);
        Assert.AreEqual(10, row.Queue.MinMs);
        Assert.AreEqual(30, row.Queue.MaxMs);
        Assert.AreEqual(2, row.Processing.Count);
        Assert.AreEqual(200, row.Processing.AvgMs);
    }

    [TestMethod]
    public async Task GetFailedMessageInsights_returns_recent_failed_error_details()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var eventId = Id("evt-insight");
        var receiver = Id("receiver");
        await store.StoreMessage(SampleMessage(
            eventId,
            Id("msg-insight"),
            MessageType.ErrorResponse,
            from.AddMinutes(1),
            endpointId: receiver,
            errorText: "Downstream timeout"));
        await store.UploadFailedMessage(eventId, "session-1", receiver, SampleFailedEvent(eventId, from.AddMinutes(1), "Downstream timeout", receiver));

        var insights = await store.GetFailedMessageInsights(from);
        var row = insights.Single(i => i.EventId == eventId);

        Assert.AreEqual(receiver, row.EndpointId);
        Assert.AreEqual("OrderPlaced", row.EventTypeId);
        Assert.AreEqual("Downstream timeout", row.ErrorText);
    }

    [TestMethod]
    public async Task GetTimeSeriesMetrics_buckets_message_type_counts()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddMinutes(5);
        var bucketTime = from.AddMinutes(1);
        var bucketKey = bucketTime.ToString("o")[..13];
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-ts-published"), Id("msg-ts-published"), MessageType.EventRequest, bucketTime, endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-ts-handled"), Id("msg-ts-handled"), MessageType.ResolutionResponse, bucketTime, endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-ts-failed"), Id("msg-ts-failed"), MessageType.ErrorResponse, bucketTime, endpointId: receiver, fromAddress: publisher));

        var timeSeries = await store.GetTimeSeriesMetrics(from, substringLength: 13, bucketLabel: "hour");
        var bucket = timeSeries.DataPoints.Single(dp => dp.Timestamp == bucketKey);

        Assert.AreEqual("hour", timeSeries.BucketSize);
        Assert.AreEqual(1, bucket.Published);
        Assert.AreEqual(1, bucket.Handled);
        Assert.AreEqual(1, bucket.Failed);
    }

    private static MessageEntity SampleMessage(
        string eventId,
        string messageId,
        MessageType messageType,
        DateTime enqueuedTimeUtc,
        string endpointId = "receiver",
        string fromAddress = "publisher",
        long? queueTimeMs = null,
        long? processingTimeMs = null,
        string? errorText = null) => new()
    {
        EventId = eventId,
        MessageId = messageId,
        EndpointId = endpointId,
        SessionId = "session-1",
        CorrelationId = "corr-1",
        EventTypeId = "OrderPlaced",
        EnqueuedTimeUtc = enqueuedTimeUtc,
        MessageType = messageType,
        EndpointRole = EndpointRole.Subscriber,
        From = fromAddress,
        To = endpointId,
        QueueTimeMs = queueTimeMs,
        ProcessingTimeMs = processingTimeMs,
        MessageContent = new MessageContent
        {
            ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
        },
        DeadLetterErrorDescription = errorText,
    };

    private static UnresolvedEvent SampleFailedEvent(string eventId, DateTime enqueuedTimeUtc, string errorText, string endpointId) => new()
    {
        EventId = eventId,
        SessionId = "session-1",
        EndpointId = endpointId,
        EnqueuedTimeUtc = enqueuedTimeUtc,
        UpdatedAt = enqueuedTimeUtc,
        CorrelationId = "corr-1",
        EndpointRole = EndpointRole.Subscriber,
        MessageType = MessageType.ErrorResponse,
        EventTypeId = "OrderPlaced",
        To = endpointId,
        From = "publisher",
        ResolutionStatus = ResolutionStatus.Failed,
        DeadLetterErrorDescription = errorText,
        MessageContent = new MessageContent
        {
            ErrorContent = new ErrorContent { ErrorText = errorText },
        },
    };
}
