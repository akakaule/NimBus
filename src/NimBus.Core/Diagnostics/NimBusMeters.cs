using System.Diagnostics.Metrics;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Holds the framework-level <see cref="Meter"/> instances and pre-created instruments used by
/// every NimBus instrumentation site. Public so transport providers and the
/// <c>NimBus.OpenTelemetry</c> package can record measurements on the canonical meters;
/// external consumers should normally subscribe via the names exposed on
/// <see cref="NimBusInstrumentation"/> rather than referencing these instances.
/// </summary>
public static class NimBusMeters
{
    public static readonly Meter Publisher = new(NimBusInstrumentation.PublisherMeterName);

    public static readonly Counter<long> MessagesPublished = Publisher.CreateCounter<long>(
        "nimbus.message.published", "{messages}", "Messages handed to a transport sender.");

    public static readonly Histogram<double> PublishDuration = Publisher.CreateHistogram<double>(
        "nimbus.message.publish.duration", "ms", "Time spent in the publish path, including transport send.");

    public static readonly Counter<long> PublishFailed = Publisher.CreateCounter<long>(
        "nimbus.message.publish.failed", "{messages}", "Publish attempts that threw.");

    public static readonly Meter Consumer = new(NimBusInstrumentation.ConsumerMeterName);

    public static readonly Counter<long> MessagesReceived = Consumer.CreateCounter<long>(
        "nimbus.message.received", "{messages}", "Messages dispatched to the consumer pipeline.");

    public static readonly Counter<long> MessagesProcessed = Consumer.CreateCounter<long>(
        "nimbus.message.processed", "{messages}", "Messages whose pipeline run completed (any outcome).");

    public static readonly Histogram<double> ProcessDuration = Consumer.CreateHistogram<double>(
        "nimbus.message.process.duration", "ms", "Time spent processing a message through the consumer pipeline.");

    public static readonly Histogram<double> QueueWait = Consumer.CreateHistogram<double>(
        "nimbus.message.queue_wait", "ms", "Time the message spent in the broker before consumer entry.");

    public static readonly Histogram<double> EndToEndLatency = Consumer.CreateHistogram<double>(
        "nimbus.message.e2e_latency", "ms", "End-to-end broker-enqueue to handler-completion latency.");

    public static readonly Meter Outbox = new(NimBusInstrumentation.OutboxMeterName);

    public static readonly Counter<long> OutboxEnqueued = Outbox.CreateCounter<long>(
        "nimbus.outbox.enqueued", "{messages}", "Messages persisted to the transactional outbox.");

    public static readonly Counter<long> OutboxDispatched = Outbox.CreateCounter<long>(
        "nimbus.outbox.dispatched", "{messages}", "Outbox rows successfully forwarded by the dispatcher.");

    public static readonly Histogram<double> OutboxDispatchDuration = Outbox.CreateHistogram<double>(
        "nimbus.outbox.dispatch.duration", "ms", "Time the dispatcher took to forward a single outbox row.");

    public static readonly Meter DeferredProcessor = new(NimBusInstrumentation.DeferredProcessorMeterName);

    public static readonly Counter<long> DeferredParked = DeferredProcessor.CreateCounter<long>(
        "nimbus.deferred.parked", "{messages}", "Messages parked by the session-block deferred path.");

    public static readonly Counter<long> DeferredReplayed = DeferredProcessor.CreateCounter<long>(
        "nimbus.deferred.replayed", "{messages}", "Messages replayed from the deferred subscription on session unblock.");

    public static readonly Histogram<double> DeferredReplayDuration = DeferredProcessor.CreateHistogram<double>(
        "nimbus.deferred.replay.duration", "ms", "Time taken to replay one batch of deferred messages.");

    public static readonly Meter Resolver = new(NimBusInstrumentation.ResolverMeterName);

    public static readonly Counter<long> ResolverOutcomeWritten = Resolver.CreateCounter<long>(
        "nimbus.resolver.outcome_written", "{records}", "Outcome rows written by the resolver.");

    public static readonly Counter<long> ResolverAuditWritten = Resolver.CreateCounter<long>(
        "nimbus.resolver.audit_written", "{records}", "Audit rows written by the resolver.");

    public static readonly Histogram<double> ResolverWriteDuration = Resolver.CreateHistogram<double>(
        "nimbus.resolver.write.duration", "ms", "Time the resolver took to persist a single outcome or audit row.");

    public static readonly Meter Store = new(NimBusInstrumentation.StoreMeterName);

    public static readonly Histogram<double> StoreOperationDuration = Store.CreateHistogram<double>(
        "nimbus.store.operation.duration", "ms", "Time taken by a single message-store operation.");

    public static readonly Counter<long> StoreOperationFailed = Store.CreateCounter<long>(
        "nimbus.store.operation.failed", "{ops}", "Message-store operations that threw.");
}
