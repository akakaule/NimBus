#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public class AddNimBusInstrumentationTests
{
    [TestMethod]
    public void Services_AddNimBusInstrumentation_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddNimBusInstrumentation();
        services.AddNimBusInstrumentation();
        services.AddNimBusInstrumentation(opts => opts.Verbose = true);

        var markers = services.Where(d => d.ServiceType.Name == "NimBusInstrumentationMarker").ToList();
        Assert.AreEqual(1, markers.Count, "marker registered once regardless of call count");
    }

    [TestMethod]
    public void MeterProvider_AddNimBusInstrumentation_registers_every_meter()
    {
        var collected = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(collected)
            .Build()!;

        NimBusMeters.MessagesPublished.Add(1);
        NimBusMeters.MessagesReceived.Add(1);
        NimBusMeters.OutboxEnqueued.Add(1);
        NimBusMeters.DeferredParked.Add(1);
        NimBusMeters.ResolverOutcomeWritten.Add(1);
        NimBusMeters.StoreOperationFailed.Add(1);

        provider.ForceFlush();

        var meterNames = collected.Select(m => m.MeterName).Distinct().ToList();
        CollectionAssert.AreEquivalent(NimBusInstrumentation.AllMeterNames.ToList(), meterNames);
    }

    [TestMethod]
    public void TracerProvider_AddNimBusInstrumentation_registers_every_source()
    {
        var collected = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(collected)
            .Build()!;

        using (NimBusActivitySources.Publisher.StartActivity("publish test")) { }
        using (NimBusActivitySources.Consumer.StartActivity("process test")) { }
        using (NimBusActivitySources.Outbox.StartActivity("NimBus.Outbox.Dispatch")) { }
        using (NimBusActivitySources.DeferredProcessor.StartActivity("NimBus.DeferredProcessor.Park")) { }
        using (NimBusActivitySources.Resolver.StartActivity("NimBus.Resolver.RecordOutcome")) { }
        using (NimBusActivitySources.Store.StartActivity("NimBus.Store.Get")) { }

        provider.ForceFlush();

        var sourceNames = collected.Select(a => a.Source.Name).Distinct().ToList();
        CollectionAssert.AreEquivalent(NimBusInstrumentation.AllActivitySourceNames.ToList(), sourceNames);
    }
}

[TestClass]
public class InstrumentingSenderDecoratorTests
{
    [TestMethod]
    public async Task Send_emits_publisher_span_with_messaging_attributes()
    {
        var collected = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(collected)
            .Build()!;

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);

        var message = new Message
        {
            EventId = "evt-1",
            MessageId = "msg-1",
            CorrelationId = "corr-1",
            SessionId = "session-1",
            EventTypeId = "Test.Event.v1",
            To = "test-endpoint",
        };

        await sut.Send(message);
        provider.ForceFlush();

        Assert.AreEqual(1, inner.SendCount, "inner sender invoked exactly once");

        var span = collected.SingleOrDefault(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        Assert.IsNotNull(span);
        Assert.AreEqual("publish test-endpoint", span.DisplayName);
        Assert.AreEqual(ActivityKind.Producer, span.Kind);

        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.AreEqual(MessagingSystem.InMemory, tags[MessagingAttributes.System]);
        Assert.AreEqual("publish", tags[MessagingAttributes.OperationType]);
        Assert.AreEqual("test-endpoint", tags[MessagingAttributes.DestinationName]);
        Assert.AreEqual("Test.Event.v1", tags[MessagingAttributes.NimBusEventType]);
        Assert.AreEqual("msg-1", tags[MessagingAttributes.MessageId]);
        Assert.AreEqual("corr-1", tags[MessagingAttributes.MessageConversationId]);
        Assert.AreEqual("session-1", tags[MessagingAttributes.NimBusSessionKey]);
    }

    [TestMethod]
    public async Task Send_failure_records_error_and_increments_failed_counter()
    {
        var collected = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(collected)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var inner = new RecordingSender { Throw = new InvalidOperationException("boom") };
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);

        var message = new Message { EventId = "e", MessageId = "m", To = "t", EventTypeId = "T" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.Send(message));
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        var span = collected.SingleOrDefault(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        Assert.IsNotNull(span);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);

        var failed = metrics.FirstOrDefault(m => m.Name == "nimbus.message.publish.failed");
        Assert.IsNotNull(failed);
    }

    [TestMethod]
    public async Task Send_round_trip_propagates_traceparent_via_Activity_Current()
    {
        var collected = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(collected)
            .Build()!;

        // Simulate an outer caller activity (e.g., an HTTP request).
        using var outerSource = new ActivitySource("test.outer");
        using var outerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("test.outer")
            .AddInMemoryExporter(collected)
            .Build()!;

        using var outer = outerSource.StartActivity("incoming-request");

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);
        await sut.Send(new Message { To = "queue", EventTypeId = "evt", EventId = "e", MessageId = "m" });

        provider.ForceFlush();
        outerProvider.ForceFlush();

        var publish = collected.Single(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        Assert.AreEqual(outer!.TraceId, publish.TraceId, "publisher span shares trace id with outer activity");
    }

    // ── Scheduled-message operations (spec 025) ──────────────────────────

    [TestMethod]
    public async Task ScheduleWithHandle_emits_bounded_schedule_operations_counter()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);
        var message = new Message
        {
            EventId = "e",
            MessageId = "order-42:payment-timeout:1",
            ScheduledMessageId = "order-42:payment-timeout:1",
            SessionId = "order-42",
            CorrelationId = "conversation-7",
            To = "t",
            EventTypeId = "PaymentTimedOut",
        };

        await sut.ScheduleMessageWithHandle(message, DateTimeOffset.UtcNow.AddHours(1));
        meterProvider.ForceFlush();

        var counter = metrics.FirstOrDefault(m => m.Name == "nimbus.message.schedule.operations");
        Assert.IsNotNull(counter, "The schedule-operations counter must be emitted");
        var points = new List<MetricPoint>();
        foreach (var point in counter.GetMetricPoints()) points.Add(point);
        var tags = new Dictionary<string, string?>();
        foreach (var tag in points.Single().Tags) tags[tag.Key] = tag.Value?.ToString();
        Assert.AreEqual("schedule", tags[MessagingAttributes.NimBusScheduleOperation]);
        Assert.AreEqual("broker", tags[MessagingAttributes.NimBusScheduleMode]);
        Assert.AreEqual("scheduled", tags[MessagingAttributes.NimBusOutcome]);
        // High-cardinality guard: TimeoutId/MessageId/SessionId/CorrelationId are
        // span/log fields only, never metric dimensions.
        Assert.IsFalse(tags.ContainsKey(MessagingAttributes.MessageId));
        Assert.IsFalse(tags.ContainsKey(MessagingAttributes.NimBusSessionKey));
        Assert.IsFalse(tags.ContainsKey(MessagingAttributes.MessageConversationId));
    }

    [TestMethod]
    public async Task CancelScheduled_emits_cancel_operation_with_outcome()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);
        var handle = new ScheduledMessageHandle("timeout-1", 1L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        var outcome = await sut.CancelScheduledMessage(handle);
        meterProvider.ForceFlush();

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancellationRequested, outcome);
        var counter = metrics.FirstOrDefault(m => m.Name == "nimbus.message.schedule.operations");
        Assert.IsNotNull(counter);
        var points = new List<MetricPoint>();
        foreach (var point in counter.GetMetricPoints()) points.Add(point);
        var tags = new Dictionary<string, string?>();
        foreach (var tag in points.Single().Tags) tags[tag.Key] = tag.Value?.ToString();
        Assert.AreEqual("cancel", tags[MessagingAttributes.NimBusScheduleOperation]);
        Assert.AreEqual("cancellation_requested", tags[MessagingAttributes.NimBusOutcome]);
    }

    private sealed class RecordingSender : ISender
    {
        public int SendCount;
        public Exception? Throw;

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SendCount += messages.Count();
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(1L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
