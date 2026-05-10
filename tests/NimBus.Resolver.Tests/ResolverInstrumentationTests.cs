#pragma warning disable CA1707, CA1515, CA2007, CA1859, CA1861
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Broker.Services;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Resolver.Tests;

[TestClass]
public class ResolverInstrumentationTests
{
    [TestMethod]
    public async Task Outcome_write_emits_span_per_terminal_status()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new ResolverServiceTests.FakeCosmosDbClient();
        var service = new ResolverService(cosmos);

        await service.Handle(NewMessage(MessageType.ResolutionResponse));   // Completed
        await service.Handle(NewMessage(MessageType.SkipResponse));         // Skipped
        await service.Handle(NewMessage(MessageType.ErrorResponse));        // Failed
        await service.Handle(NewMessage(MessageType.DeferralResponse));     // Deferred
        await service.Handle(NewMessage(MessageType.EventRequest));         // Pending
        await service.Handle(NewMessage(MessageType.UnsupportedResponse)); // Unsupported

        var outcomeSpans = capture.Activities
            .Where(a => a.OperationName == "NimBus.Resolver.RecordOutcome")
            .ToList();
        Assert.AreEqual(6, outcomeSpans.Count);

        var outcomeTags = outcomeSpans
            .Select(s => (string?)s.GetTagItem(MessagingAttributes.NimBusOutcome))
            .ToList();
        CollectionAssert.AreEquivalent(
            new[] { "completed", "skipped", "failed", "deferred", "pending", "unsupported" },
            outcomeTags);

        foreach (var span in outcomeSpans)
        {
            Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
            Assert.IsNotNull(span.GetTagItem(MessagingAttributes.NimBusEndpoint));
        }

        var counter = capture.Sum("nimbus.resolver.outcome_written");
        Assert.AreEqual(6, counter);

        var durationCount = capture.HistogramCount("nimbus.resolver.write.duration");
        Assert.IsTrue(durationCount >= 6, $"Expected at least 6 duration observations, got {durationCount}");
    }

    [TestMethod]
    public async Task Outcome_write_counter_carries_endpoint_and_outcome_tags()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new ResolverServiceTests.FakeCosmosDbClient();
        var service = new ResolverService(cosmos);

        await service.Handle(NewMessage(MessageType.ResolutionResponse, from: "BillingEndpoint"));

        var counterRow = capture.Measurements
            .Where(m => m.Name == "nimbus.resolver.outcome_written")
            .Single(m => string.Equals(m.Tags.GetValueOrDefault(MessagingAttributes.NimBusOutcome)?.ToString(), "completed", StringComparison.Ordinal));
        Assert.AreEqual("BillingEndpoint", counterRow.Tags[MessagingAttributes.NimBusEndpoint]);
        Assert.AreEqual(1, counterRow.Value);
    }

    [TestMethod]
    public async Task RetryRequest_emits_audit_span_and_counter()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new ResolverServiceTests.FakeCosmosDbClient();
        var service = new ResolverService(cosmos);

        await service.Handle(NewMessage(MessageType.RetryRequest, to: "BillingEndpoint"));

        var auditSpan = capture.Activities.Single(a => a.OperationName == "NimBus.Resolver.RecordAudit");
        Assert.AreEqual(ActivityKind.Internal, auditSpan.Kind);
        Assert.AreEqual("retry", auditSpan.GetTagItem(MessagingAttributes.NimBusAuditType));
        Assert.AreEqual("BillingEndpoint", auditSpan.GetTagItem(MessagingAttributes.NimBusEndpoint));
        Assert.AreEqual(ActivityStatusCode.Ok, auditSpan.Status);

        var counterRow = capture.Measurements.Single(m => m.Name == "nimbus.resolver.audit_written");
        Assert.AreEqual("retry", counterRow.Tags[MessagingAttributes.NimBusAuditType]);
        Assert.AreEqual("BillingEndpoint", counterRow.Tags[MessagingAttributes.NimBusEndpoint]);
        Assert.AreEqual(1, counterRow.Value);
    }

    [TestMethod]
    public async Task Outcome_write_failure_records_error_status_and_error_type_tag()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new ResolverServiceTests.FakeCosmosDbClient
        {
            UploadException = new StorageProviderTransientException("throttled", retryAfter: null),
        };
        var service = new ResolverService(cosmos);

        await service.Handle(NewMessage(MessageType.ResolutionResponse));

        var outcomeSpan = capture.Activities.Single(a => a.OperationName == "NimBus.Resolver.RecordOutcome");
        Assert.AreEqual(ActivityStatusCode.Error, outcomeSpan.Status);
        Assert.AreEqual(typeof(StorageProviderTransientException).FullName, outcomeSpan.GetTagItem(MessagingAttributes.ErrorType));

        // Counter still increments on failure so failure attribution stays observable.
        var counterRow = capture.Measurements.Single(m => m.Name == "nimbus.resolver.outcome_written");
        Assert.AreEqual(1, counterRow.Value);
        Assert.AreEqual(typeof(StorageProviderTransientException).FullName,
            counterRow.Tags[MessagingAttributes.ErrorType]);
    }

    [TestMethod]
    public async Task Audit_write_failure_records_error_status_and_error_type_tag()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new ResolverServiceTests.FakeCosmosDbClient
        {
            StoreAuditException = new InvalidOperationException("audit blew up"),
        };
        var service = new ResolverService(cosmos);

        await service.Handle(NewMessage(MessageType.RetryRequest));

        var auditSpan = capture.Activities.Single(a => a.OperationName == "NimBus.Resolver.RecordAudit");
        Assert.AreEqual(ActivityStatusCode.Error, auditSpan.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, auditSpan.GetTagItem(MessagingAttributes.ErrorType));

        var counterRow = capture.Measurements.Single(m => m.Name == "nimbus.resolver.audit_written");
        Assert.AreEqual(1, counterRow.Value);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, counterRow.Tags[MessagingAttributes.ErrorType]);
    }

    private static ResolverServiceTests.FakeMessageContext NewMessage(
        MessageType messageType,
        string to = "BillingEndpoint",
        string from = "StorefrontEndpoint")
    {
        return new ResolverServiceTests.FakeMessageContext
        {
            EventId = $"event-{Guid.NewGuid()}",
            MessageId = "msg-1",
            CorrelationId = "corr-1",
            SessionId = "session-1",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = to,
            MessageType = messageType,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            },
            EventTypeId = "OrderPlaced",
            EnqueuedTimeUtc = DateTime.UtcNow,
        };
    }
}

internal sealed class ResolverTelemetryCapture : IDisposable
{
    public List<Activity> Activities { get; } = new();
    public List<TelemetryMeasurement> Measurements { get; } = new();
    public List<HistogramObservation> Histograms { get; } = new();

    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    private ResolverTelemetryCapture()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NimBusInstrumentation.ResolverActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NimBusInstrumentation.ResolverMeterName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Measurements.Add(new TelemetryMeasurement(instrument.Name, value, ToDictionary(tags))));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Histograms.Add(new HistogramObservation(instrument.Name, value, ToDictionary(tags))));
        _meterListener.Start();
    }

    public static ResolverTelemetryCapture Start() => new();

    public long Sum(string instrumentName) => Measurements.Where(m => m.Name == instrumentName).Sum(m => m.Value);

    public int HistogramCount(string instrumentName) => Histograms.Count(h => h.Name == instrumentName);

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags) dict[tag.Key] = tag.Value;
        return dict;
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }
}

internal sealed record TelemetryMeasurement(string Name, long Value, IReadOnlyDictionary<string, object?> Tags);
internal sealed record HistogramObservation(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
