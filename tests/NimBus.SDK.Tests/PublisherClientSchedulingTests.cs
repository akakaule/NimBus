#pragma warning disable CA1707, CA2007, CS0618
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CloudEvents;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class PublisherClientSchedulingTests
{
    private static readonly DateTimeOffset Due = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Schedule: identity, lineage, and handle ─────────────────────────

    [TestMethod]
    public async Task Schedule_StampsTimeoutIdAsMessageIdAndScheduledMessageIdMarker()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        var handle = await publisher.Schedule(NewEvent(), Due, CreateContext(), "order-42:payment-timeout:1");

        var sent = (Message)sender.Scheduled.Single().Message;
        Assert.AreEqual("order-42:payment-timeout:1", sent.MessageId,
            "TimeoutId must be the deterministic MessageId of the first delivery");
        Assert.AreEqual("order-42:payment-timeout:1", sent.ScheduledMessageId,
            "TimeoutId must be carried as the ScheduledMessageId marker");
        Assert.AreEqual(Due, sent.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("order-42:payment-timeout:1", handle.TimeoutId);
        Assert.AreEqual(ScheduledMessageHandleKind.BrokerSequenceNumber, handle.Kind);
        Assert.AreEqual(7L, handle.SequenceNumber);
    }

    [TestMethod]
    public async Task Schedule_PreservesWorkflowIdentityAndLineageFromContext()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var context = CreateContext(
            messageId: "inventory-reserved-1",
            parentMessageId: "reserve-inventory-1",
            originatingMessageId: "order-placed-1");

        await publisher.Schedule(NewEvent(), Due, context, "order-42:payment-timeout:1");

        var sent = sender.Scheduled.Single().Message;
        Assert.AreEqual("order-42", sent.SessionId);
        Assert.AreEqual("conversation-7", sent.CorrelationId);
        Assert.AreEqual("inventory-reserved-1", sent.ParentMessageId);
        Assert.AreEqual("order-placed-1", sent.OriginatingMessageId);
        Assert.AreEqual("OrderOrchestrator", sent.OriginatingFrom);
    }

    [TestMethod]
    public async Task Schedule_FirstHopWithMissingLegacyLineage_UsesInboundMessageAsOrigin()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var context = CreateContext(
            messageId: "order-placed-1",
            parentMessageId: Constants.Self,
            originatingMessageId: Constants.Self);

        await publisher.Schedule(NewEvent(), Due, context, "order-42:payment-timeout:1");

        var sent = sender.Scheduled.Single().Message;
        Assert.AreEqual("order-placed-1", sent.ParentMessageId);
        Assert.AreEqual("order-placed-1", sent.OriginatingMessageId);
    }

    [TestMethod]
    public async Task Schedule_CloudEventsEnabled_SetsCloudEventIdToTimeoutId()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: true);

        await publisher.Schedule(NewEvent(), Due, CreateContext(), "order-42:payment-timeout:1");

        var sent = (Message)sender.Scheduled.Single().Message;
        Assert.IsNotNull(sent.CloudEvent);
        Assert.AreEqual("order-42:payment-timeout:1", sent.CloudEvent.CloudEvent.Id);
        Assert.AreEqual("order-42:payment-timeout:1", sent.ScheduledMessageId);
    }

    [TestMethod]
    public async Task Schedule_NativeMode_HasNoCloudEventEnvelope()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await publisher.Schedule(NewEvent(), Due, CreateContext(), "order-42:payment-timeout:1");

        Assert.IsNull(((Message)sender.Scheduled.Single().Message).CloudEvent);
    }

    [TestMethod]
    public async Task Schedule_NormalizesDueTimeToUtc_AndAllowsPastDueTimes()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var pastLocal = new DateTimeOffset(2020, 1, 1, 13, 0, 0, TimeSpan.FromHours(2));

        await publisher.Schedule(NewEvent(), pastLocal, CreateContext(), "order-42:payment-timeout:1");

        var (message, dueTime) = sender.Scheduled.Single();
        Assert.AreEqual(TimeSpan.Zero, dueTime.Offset, "Due time must be normalized to UTC");
        Assert.AreEqual(pastLocal.ToUniversalTime(), dueTime, "A past due time is allowed (immediately eligible)");
        Assert.AreEqual(pastLocal.ToUniversalTime(), ((Message)message).ScheduledEnqueueTimeUtc);
    }

    [TestMethod]
    public async Task Schedule_PropagatesCancellationToken()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        using var cancellation = new CancellationTokenSource();

        await publisher.Schedule(NewEvent(), Due, CreateContext(), "timeout-1", cancellation.Token);

        Assert.AreEqual(cancellation.Token, sender.LastCancellationToken);
    }

    // ── Schedule: validation ────────────────────────────────────────────

    [TestMethod]
    public async Task Schedule_BlankTimeoutId_Throws()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => publisher.Schedule(NewEvent(), Due, CreateContext(), " "));
        Assert.AreEqual(0, sender.Scheduled.Count);
    }

    [TestMethod]
    public async Task Schedule_TimeoutIdOverServiceBusMessageIdLimit_Throws()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var tooLong = new string('x', 129);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => publisher.Schedule(NewEvent(), Due, CreateContext(), tooLong));
        Assert.AreEqual(0, sender.Scheduled.Count);
    }

    [TestMethod]
    public async Task Schedule_ExactlyMaxLengthTimeoutId_IsAccepted()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var maxLength = new string('x', 128);

        await publisher.Schedule(NewEvent(), Due, CreateContext(), maxLength);

        Assert.AreEqual(maxLength, sender.Scheduled.Single().Message.MessageId);
    }

    [TestMethod]
    public async Task Schedule_NullEventOrContext_Throws()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => publisher.Schedule(null, Due, CreateContext(), "timeout-1"));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => publisher.Schedule(NewEvent(), Due, null, "timeout-1"));
    }

    [TestMethod]
    public async Task Schedule_MissingInboundIdentity_Throws()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var contexts = new[] { CreateContext(), CreateContext(), CreateContext() };
        contexts[0].MessageId = string.Empty;
        contexts[1].SessionId = string.Empty;
        contexts[2].CorrelationId = string.Empty;

        foreach (var context in contexts)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => publisher.Schedule(NewEvent(), Due, context, "timeout-1"));
        }

        Assert.AreEqual(0, sender.Scheduled.Count);
    }

    [TestMethod]
    public async Task Schedule_InvalidEvent_PreservesExistingValidation()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ValidationException>(
            () => publisher.Schedule(new TimeoutEvent(), Due, CreateContext(), "timeout-1"));
        Assert.AreEqual(0, sender.Scheduled.Count);
    }

    // ── CancelScheduled ─────────────────────────────────────────────────

    [TestMethod]
    public async Task CancelScheduled_DirectSender_AcceptsShapeValidHandleWithoutPairVerification()
    {
        // Direct mode is sequence-only best effort: NimBus validates kind and
        // shape but CANNOT verify TimeoutId↔sequence pairing (documented).
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var handle = new ScheduledMessageHandle("any-timeout-id", 42L, ScheduledMessageHandleKind.BrokerSequenceNumber);

        var outcome = await publisher.CancelScheduled(handle);

        Assert.AreEqual(ScheduledMessageCancellationOutcome.CancellationRequested, outcome);
        Assert.AreEqual(42L, sender.CancelledSequences.Single());
    }

    [TestMethod]
    public async Task CancelScheduled_NullOrMalformedHandle_Throws()
    {
        var sender = new SchedulingCapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => publisher.CancelScheduled(null));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => publisher.CancelScheduled(
            new ScheduledMessageHandle("", 1L, ScheduledMessageHandleKind.BrokerSequenceNumber)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => publisher.CancelScheduled(
            new ScheduledMessageHandle("timeout-1", 0L, ScheduledMessageHandleKind.BrokerSequenceNumber)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => publisher.CancelScheduled(
            new ScheduledMessageHandle("timeout-1", 1L, (ScheduledMessageHandleKind)99)));
        Assert.AreEqual(0, sender.CancelledSequences.Count);
    }

    // ── Compatibility ───────────────────────────────────────────────────

    [TestMethod]
    public async Task CustomPublisherWithoutNewMembers_GetsNotSupportedDefaults()
    {
        IPublisherClient legacy = new LegacyPublisherClient();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => legacy.Schedule(NewEvent(), Due, CreateContext(), "timeout-1"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => legacy.CancelScheduled(new ScheduledMessageHandle("timeout-1", 1L, ScheduledMessageHandleKind.BrokerSequenceNumber)));
    }

    [TestMethod]
    public async Task LegacyConcreteSchedule_DirectSender_StillReturnsSequenceNumber()
    {
        var sender = new SchedulingCapturingSender();
        var publisher = CreatePublisher(sender, useCloudEvents: false);

        var sequence = await publisher.Schedule(NewEvent(), Due);

        Assert.AreEqual(7L, sequence);
        Assert.IsNull(((Message)sender.Scheduled.Single().Message).ScheduledMessageId,
            "The legacy bridge does not stamp the marker — identity requires the handle API");
    }

    [TestMethod]
    public async Task LegacyConcreteCancelScheduled_OutboxSender_StillThrowsNotSupported()
    {
        var publisher = new PublisherClient(new OutboxSender(new NoopOutbox()), "OrderOrchestrator");

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => publisher.CancelScheduled(42L));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static PublisherClient CreatePublisher(SchedulingCapturingSender sender, bool useCloudEvents) =>
        new(sender, "OrderOrchestrator", useCloudEvents
            ? new CloudEventPublisherOptions
            {
                Source = new Uri("urn:test:order-orchestrator"),
                Time = _ => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
            : null!);

    private static EventHandlerContext CreateContext(
        string messageId = "inventory-reserved-1",
        string parentMessageId = "reserve-inventory-1",
        string originatingMessageId = "order-placed-1") =>
        new()
        {
            MessageId = messageId,
            SessionId = "order-42",
            CorrelationId = "conversation-7",
            ParentMessageId = parentMessageId,
            OriginatingMessageId = originatingMessageId,
        };

    private static TimeoutEvent NewEvent() => new() { OrderId = "order-42" };

    private sealed class TimeoutEvent : Event
    {
        [Required]
        public string OrderId { get; set; } = string.Empty;

        public override string GetSessionId() => OrderId;
    }

    private sealed class SchedulingCapturingSender : ISender
    {
        public List<(IMessage Message, DateTimeOffset DueTime)> Scheduled { get; } = new();
        public List<long> CancelledSequences { get; } = new();
        public CancellationToken LastCancellationToken { get; private set; }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            Scheduled.Add((message, scheduledEnqueueTime));
            return Task.FromResult(7L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
        {
            CancelledSequences.Add(sequenceNumber);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopOutbox : IOutbox
    {
        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LegacyPublisherClient : IPublisherClient
    {
        public Task Publish(IEvent @event) => Task.CompletedTask;
        public Task Publish(IMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(IEvent @event, string sessionId, string correlationId) => Task.CompletedTask;
        public Task Publish(IEvent @event, string sessionId, string correlationId, string messageId) => Task.CompletedTask;
        public Task PublishBatch(IEnumerable<IEvent> events, string correlationId = null) => Task.CompletedTask;
        public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events) => new[] { events };
    }
}
