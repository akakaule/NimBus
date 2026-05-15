#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class DeferredMessageProcessorTests
{
    // ── Constructor ─────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new DeferredMessageProcessor(null!));
    }

    [TestMethod]
    public void Constructor_DefaultSubscriptionName_UsesConstant()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client);

        // Verify it doesn't throw and can be used
        Assert.IsNotNull(sut);
    }

    // ── Argument validation ─────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_NullSessionId_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync(null!, "my-topic"));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptySessionId_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("", "my-topic"));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_NullTopicName_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", null!));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptyTopicName_ThrowsArgumentException()
    {
        var sut = new DeferredMessageProcessor(new RecordingServiceBusClient());

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", ""));
    }

    // ── SessionCannotBeLocked ───────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_SessionCannotBeLocked_ReturnsGracefully()
    {
        var client = new RecordingServiceBusClient
        {
            AcceptSessionException = new ServiceBusException("no session", ServiceBusFailureReason.SessionCannotBeLocked)
        };
        var sut = new DeferredMessageProcessor(client);

        // Should not throw
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");
    }

    // ── Empty batch ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_EmptyBatch_SendsNothingAndCompletes()
    {
        var client = new RecordingServiceBusClient();
        // No batches configured → receiver returns empty list on first call
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual(0, client.Sender.SentMessages.Count);
        Assert.AreEqual(0, client.SessionReceiver.CompletedMessages.Count);
    }

    // ── Processing ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_SingleBatch_SortsByDeferralSequenceAndRepublishes()
    {
        var client = new RecordingServiceBusClient();
        var msg3 = CreateReceivedMessage("corr-3", deferralSequence: 3);
        var msg1 = CreateReceivedMessage("corr-1", deferralSequence: 1);
        var msg2 = CreateReceivedMessage("corr-2", deferralSequence: 2);

        // Messages arrive out of order
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg3, msg1, msg2 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        // Should be republished in DeferralSequence order: 1, 2, 3
        Assert.AreEqual(3, client.Sender.SentMessages.Count);
        Assert.AreEqual("corr-1", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-2", client.Sender.SentMessages[1].CorrelationId);
        Assert.AreEqual("corr-3", client.Sender.SentMessages[2].CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CompletesAllDeferredMessages()
    {
        var client = new RecordingServiceBusClient();
        var msg1 = CreateReceivedMessage("corr-1", deferralSequence: 1);
        var msg2 = CreateReceivedMessage("corr-2", deferralSequence: 2);
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg1, msg2 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual(2, client.SessionReceiver.CompletedMessages.Count);
        Assert.AreSame(msg1, client.SessionReceiver.CompletedMessages[0]);
        Assert.AreSame(msg2, client.SessionReceiver.CompletedMessages[1]);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_ExcludesDeferredProperties()
    {
        var client = new RecordingServiceBusClient();
        // Production code path: SendToDeferredSubscription sets To = "Deferred" so
        // the deferred copy routes to the Deferred subscription on the topic. The
        // republish must NOT carry that value — it would loop back / be dropped.
        var msg = CreateReceivedMessage("corr-1", deferralSequence: 5, extraProps: new Dictionary<string, object>
        {
            { UserPropertyName.To.ToString(), "Deferred" },
            { UserPropertyName.EventId.ToString(), "event-1" },
        });
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.IsFalse(republished.ApplicationProperties.ContainsKey(UserPropertyName.OriginalSessionId.ToString()),
            "OriginalSessionId should be excluded");
        Assert.IsFalse(republished.ApplicationProperties.ContainsKey(UserPropertyName.DeferralSequence.ToString()),
            "DeferralSequence should be excluded");
        Assert.AreEqual("my-topic", republished.ApplicationProperties[UserPropertyName.To.ToString()],
            "To must be reset to the destination topic so the main `user.To = '<endpointId>'` filter matches");
        Assert.AreEqual("event-1", republished.ApplicationProperties[UserPropertyName.EventId.ToString()]);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_SetsSessionIdAndCorrelationId()
    {
        var client = new RecordingServiceBusClient();
        var msg = CreateReceivedMessage("corr-1", deferralSequence: 1);
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.AreEqual("session-1", republished.SessionId);
        Assert.AreEqual("corr-1", republished.CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_AcceptsCorrectSession()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-42", "orders");

        Assert.AreEqual("orders", client.LastTopicName);
        Assert.AreEqual(Constants.DeferredSubscriptionName, client.LastSubscriptionName);
        Assert.AreEqual("session-42", client.LastSessionId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CustomSubscriptionName_UsesProvidedName()
    {
        var client = new RecordingServiceBusClient();
        var sut = new DeferredMessageProcessor(client, "CustomDeferred");

        await sut.ProcessDeferredMessagesAsync("session-1", "orders");

        Assert.AreEqual("CustomDeferred", client.LastSubscriptionName);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CreatesPublisherForCorrectTopic()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage>
        {
            CreateReceivedMessage("corr-1", deferralSequence: 1)
        });
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        Assert.AreEqual("billing", client.LastSenderEntityPath);
    }

    // ── Multi-batch loop ────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_MultipleBatches_ProcessesAllBatchesUntilFinalShortBatch()
    {
        // Production loop receives in chunks of BatchSize (100) and exits when a
        // batch returns fewer than BatchSize messages. Feed a full first batch
        // (forces the loop to continue) plus a short second batch (forces the
        // terminating break at `messages.Count < BatchSize`).
        const int batchSize = 100;
        var client = new RecordingServiceBusClient();
        var firstBatch = Enumerable.Range(1, batchSize)
            .Select(i => CreateReceivedMessage($"corr-{i:D3}", deferralSequence: i))
            .Cast<ServiceBusReceivedMessage>()
            .ToList();
        var secondBatch = Enumerable.Range(batchSize + 1, 5)
            .Select(i => CreateReceivedMessage($"corr-{i:D3}", deferralSequence: i))
            .Cast<ServiceBusReceivedMessage>()
            .ToList();
        client.SessionReceiver.ReceiveBatches.Add(firstBatch);
        client.SessionReceiver.ReceiveBatches.Add(secondBatch);

        using var capture = ReplayTelemetryCapture.Start();
        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        Assert.AreEqual(105, client.Sender.SentMessages.Count, "All messages from both batches should be republished");
        Assert.AreEqual(105, client.SessionReceiver.CompletedMessages.Count, "All messages from both batches should be completed");
        // Cross-batch ordering: first batch precedes second.
        Assert.AreEqual("corr-001", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-100", client.Sender.SentMessages[99].CorrelationId);
        Assert.AreEqual("corr-101", client.Sender.SentMessages[100].CorrelationId);
        Assert.AreEqual("corr-105", client.Sender.SentMessages[104].CorrelationId);

        // Counter fires once per inner batch, with each batch's size.
        var replayed = capture.LongMeasurements.Where(m => m.Name == "nimbus.deferred.replayed").ToList();
        Assert.AreEqual(2, replayed.Count, "Two batches should record two counter increments");
        Assert.AreEqual(100, replayed[0].Value);
        Assert.AreEqual(5, replayed[1].Value);

        // Span carries the cumulative total across both batches.
        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(105, span.GetTagItem(MessagingAttributes.NimBusDeferredBatchSize));
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
    }

    // ── GetDeferralSequence behavior (tested via sort order) ────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_DeferralSequenceAsString_SortsCorrectly()
    {
        var client = new RecordingServiceBusClient();
        // DeferralSequence stored as string instead of int
        var msg2 = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-2",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), "2" },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });
        var msg1 = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-1",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), "1" },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });

        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg2, msg1 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        Assert.AreEqual("corr-1", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-2", client.Sender.SentMessages[1].CorrelationId);
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_MissingDeferralSequence_TreatsAsZero()
    {
        var client = new RecordingServiceBusClient();
        // One message with sequence 1, one with no sequence (should be treated as 0)
        var msgWithSeq = CreateReceivedMessage("corr-with-seq", deferralSequence: 1);
        var msgWithout = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: "corr-no-seq",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.To.ToString(), "SomeEndpoint" },
            });

        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msgWithSeq, msgWithout });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        // Missing sequence treated as 0, so it should come first
        Assert.AreEqual("corr-no-seq", client.Sender.SentMessages[0].CorrelationId);
        Assert.AreEqual("corr-with-seq", client.Sender.SentMessages[1].CorrelationId);
    }

    // ── Transient exception ─────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_TransientServiceBusException_ThrowsTransientException()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveMessagesException =
            new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var sut = new DeferredMessageProcessor(client);

        var ex = await Assert.ThrowsExceptionAsync<TransientException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", "my-topic"));

        Assert.IsInstanceOfType(ex.InnerException, typeof(ServiceBusException));
    }

    // ── Cancellation ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CancellationDuringBatch_StopsAndRecordsErrorSpan()
    {
        // Inner foreach calls ThrowIfCancellationRequested at the top of each
        // iteration. Cancelling after the first send (which the fake completes
        // synchronously) means the second iteration's check throws, so only the
        // first message is sent and completed.
        using var cts = new CancellationTokenSource();
        var client = new RecordingServiceBusClient();
        client.Sender.OnSent = _ =>
        {
            if (client.Sender.SentMessages.Count == 1) cts.Cancel();
        };

        var batch = Enumerable.Range(1, 5)
            .Select(i => CreateReceivedMessage($"corr-{i}", deferralSequence: i))
            .Cast<ServiceBusReceivedMessage>()
            .ToList();
        client.SessionReceiver.ReceiveBatches.Add(batch);

        using var capture = ReplayTelemetryCapture.Start();
        var sut = new DeferredMessageProcessor(client);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", "billing", cts.Token));

        Assert.AreEqual(1, client.Sender.SentMessages.Count, "Only the first message should have been republished");
        Assert.AreEqual(1, client.SessionReceiver.CompletedMessages.Count, "Only the first message should have been completed");

        // OperationCanceledException is not a ServiceBusException, so the inner
        // transient catch doesn't match — the outer catch records the span as
        // Error with the actual exception type.
        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        var errorType = (string?)span.GetTagItem(MessagingAttributes.ErrorType);
        Assert.IsTrue(
            errorType is not null && typeof(OperationCanceledException).IsAssignableFrom(Type.GetType(errorType)!),
            $"Expected an OperationCanceledException-derived error_type, got '{errorType}'");
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_CancellationBetweenBatches_ExitsCleanlyWithoutThrowing()
    {
        // Different cancellation path: a full batch finishes processing, then
        // the outer `while (!cancellationToken.IsCancellationRequested)` check
        // exits the loop without throwing. Span ends Ok.
        const int batchSize = 100;
        using var cts = new CancellationTokenSource();
        var client = new RecordingServiceBusClient();
        client.Sender.OnSent = _ =>
        {
            // Cancel only after the entire first batch has been republished.
            if (client.Sender.SentMessages.Count == batchSize) cts.Cancel();
        };

        var firstBatch = Enumerable.Range(1, batchSize)
            .Select(i => CreateReceivedMessage($"corr-{i:D3}", deferralSequence: i))
            .Cast<ServiceBusReceivedMessage>()
            .ToList();
        var secondBatch = new List<ServiceBusReceivedMessage>
        {
            CreateReceivedMessage("corr-should-not-process", deferralSequence: 999),
        };
        client.SessionReceiver.ReceiveBatches.Add(firstBatch);
        client.SessionReceiver.ReceiveBatches.Add(secondBatch);

        using var capture = ReplayTelemetryCapture.Start();
        var sut = new DeferredMessageProcessor(client);

        // No exception — cancellation between batches is a graceful shutdown.
        await sut.ProcessDeferredMessagesAsync("session-1", "billing", cts.Token);

        Assert.AreEqual(batchSize, client.Sender.SentMessages.Count, "Only the first batch should have been republished");
        Assert.AreEqual(batchSize, client.SessionReceiver.CompletedMessages.Count, "Only the first batch should have been completed");
        Assert.IsFalse(client.Sender.SentMessages.Any(m => m.CorrelationId == "corr-should-not-process"),
            "The second batch must not be republished after cancellation");

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
        Assert.AreEqual(batchSize, span.GetTagItem(MessagingAttributes.NimBusDeferredBatchSize));
    }

    // ── Body preservation ───────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_RepublishedMessage_PreservesBody()
    {
        var client = new RecordingServiceBusClient();
        var body = new BinaryData("{\"orderId\":42}");
        var msg = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: body,
            correlationId: "corr-1",
            properties: new Dictionary<string, object>
            {
                { UserPropertyName.DeferralSequence.ToString(), 1 },
                { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
            });
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "my-topic");

        var republished = client.Sender.SentMessages.Single();
        Assert.AreEqual("{\"orderId\":42}", republished.Body.ToString());
    }

    // ── Replay instrumentation (Phase 4.2 §2) ───────────────────────────

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_emits_replay_span_and_increments_counter()
    {
        using var capture = ReplayTelemetryCapture.Start();
        var client = new RecordingServiceBusClient();
        var msg1 = CreateReceivedMessage("corr-1", deferralSequence: 1);
        var msg2 = CreateReceivedMessage("corr-2", deferralSequence: 2);
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { msg1, msg2 });

        var sut = new DeferredMessageProcessor(client);
        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(ActivityKind.Internal, span.Kind);
        Assert.AreEqual("billing", span.GetTagItem(MessagingAttributes.NimBusEndpoint));
        Assert.AreEqual("session-1", span.GetTagItem(MessagingAttributes.NimBusSessionKey));
        Assert.AreEqual(2, span.GetTagItem(MessagingAttributes.NimBusDeferredBatchSize));
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);

        var replayed = capture.LongMeasurements.Single(m => m.Name == "nimbus.deferred.replayed");
        Assert.AreEqual(2, replayed.Value);
        Assert.AreEqual("billing", replayed.Tags[MessagingAttributes.NimBusEndpoint]);

        Assert.IsTrue(capture.HistogramObservations.Any(h => h.Name == "nimbus.deferred.replay.duration"),
            "Replay duration histogram must be recorded for the batch");
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_no_messages_records_zero_batch_size()
    {
        using var capture = ReplayTelemetryCapture.Start();
        var client = new RecordingServiceBusClient();
        // No batches → empty receive
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(0, span.GetTagItem(MessagingAttributes.NimBusDeferredBatchSize));
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
        Assert.AreEqual(0, capture.LongMeasurements.Where(m => m.Name == "nimbus.deferred.replayed").Sum(m => m.Value));
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_session_cannot_be_locked_records_zero_batch_size()
    {
        using var capture = ReplayTelemetryCapture.Start();
        var client = new RecordingServiceBusClient
        {
            AcceptSessionException = new ServiceBusException("no session", ServiceBusFailureReason.SessionCannotBeLocked)
        };
        var sut = new DeferredMessageProcessor(client);

        await sut.ProcessDeferredMessagesAsync("session-1", "billing");

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(0, span.GetTagItem(MessagingAttributes.NimBusDeferredBatchSize));
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status,
            "SessionCannotBeLocked is a graceful 'nothing to do' — span must end as Ok");
    }

    [TestMethod]
    public async Task ProcessDeferredMessagesAsync_transient_failure_records_error_status()
    {
        using var capture = ReplayTelemetryCapture.Start();
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveMessagesException =
            new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var sut = new DeferredMessageProcessor(client);

        await Assert.ThrowsExceptionAsync<TransientException>(
            () => sut.ProcessDeferredMessagesAsync("session-1", "billing"));

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Replay");
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual(typeof(TransientException).FullName, span.GetTagItem(MessagingAttributes.ErrorType));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ServiceBusReceivedMessage CreateReceivedMessage(
        string correlationId,
        int deferralSequence,
        Dictionary<string, object> extraProps = null)
    {
        var properties = new Dictionary<string, object>
        {
            { UserPropertyName.DeferralSequence.ToString(), deferralSequence },
            { UserPropertyName.OriginalSessionId.ToString(), "session-1" },
        };

        if (extraProps != null)
        {
            foreach (var kvp in extraProps)
                properties[kvp.Key] = kvp.Value;
        }

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            correlationId: correlationId,
            properties: properties);
    }

    private sealed class ReplayTelemetryCapture : IDisposable
    {
        public List<Activity> Activities { get; } = new();
        public List<ReplayMeasurement> LongMeasurements { get; } = new();
        public List<ReplayMeasurement> HistogramObservations { get; } = new();

        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        private ReplayTelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == NimBusInstrumentation.DeferredProcessorActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => Activities.Add(a),
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == NimBusInstrumentation.DeferredProcessorMeterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                LongMeasurements.Add(new ReplayMeasurement(instrument.Name, value, ToDictionary(tags))));
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                HistogramObservations.Add(new ReplayMeasurement(instrument.Name, (long)value, ToDictionary(tags))));
            _meterListener.Start();
        }

        public static ReplayTelemetryCapture Start() => new();

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>(tags.Length);
            foreach (var t in tags) dict[t.Key] = t.Value;
            return dict;
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }
    }

    private sealed record ReplayMeasurement(string Name, long Value, IReadOnlyDictionary<string, object?> Tags);
}
