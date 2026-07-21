#pragma warning disable CA1707, CA2007, CS8625
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;
using NimBus.Testing.Extensions;
using NimBus.SDK.EventHandlers;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class InboxMiddlewareTests
{
    private static readonly string[] SuccessfulProcessingOrder =
        ["check", "handler-start", "handler-end", "record"];

    [TestMethod]
    public async Task First_delivery_checks_then_handles_then_records()
    {
        var order = new List<string>();
        var store = new RecordingInboxStore(order);
        var inner = new RecordingHandler(order);
        var sut = new InboxMiddleware(inner, store);

        await sut.Handle(CreateContext("message-1"));

        CollectionAssert.AreEqual(
            SuccessfulProcessingOrder,
            order);
        Assert.AreEqual("billing", store.LastCheckedEndpointId);
        Assert.AreEqual("message-1", store.LastCheckedMessageId);
        Assert.AreEqual("billing", store.LastRecordedEndpointId);
        Assert.AreEqual("message-1", store.LastRecordedMessageId);
    }

    [TestMethod]
    public async Task Fan_out_endpoints_sharing_one_store_each_run_their_handler()
    {
        var store = new RecordingInboxStore { TrackRecords = true };
        var billingHandler = new RecordingHandler();
        var shippingHandler = new RecordingHandler();
        var billingMiddleware = new InboxMiddleware(billingHandler, store);
        var shippingMiddleware = new InboxMiddleware(shippingHandler, store);

        // Fan-out preserves the broker MessageId across endpoint copies; the first endpoint's
        // record must not turn the other endpoint's first delivery into a duplicate skip.
        await billingMiddleware.Handle(CreateContext("message-1", endpointId: "billing"));
        var shippingContext = CreateContext("message-1", endpointId: "shipping");
        await shippingMiddleware.Handle(shippingContext);

        Assert.AreEqual(1, billingHandler.HandleCalls);
        Assert.AreEqual(1, shippingHandler.HandleCalls);
        Assert.AreNotEqual(HandlerOutcome.DuplicateDetected, shippingContext.HandlerOutcome);

        var shippingDuplicate = CreateContext("message-1", endpointId: "shipping");
        await shippingMiddleware.Handle(shippingDuplicate);

        Assert.AreEqual(1, shippingHandler.HandleCalls);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, shippingDuplicate.HandlerOutcome);
    }

    [TestMethod]
    public async Task Pending_handoff_success_is_not_recorded_so_redelivery_reruns_the_handler()
    {
        var store = new RecordingInboxStore { TrackRecords = true };
        var inner = new RecordingHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.PendingHandoff,
        };
        var sut = new InboxMiddleware(inner, store);

        // The pending response and session block happen after the middleware; recording here
        // would make a redelivery skip as duplicate without recreating that pending state.
        await sut.Handle(CreateContext("message-1"));

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(0, store.RecordCalls);

        await sut.Handle(CreateContext("message-1"));

        Assert.AreEqual(2, inner.HandleCalls, "An unrecorded pending handoff must be re-run on redelivery.");
    }

    [TestMethod]
    public async Task Duplicate_skips_inner_sets_outcome_and_emits_lifecycle_context()
    {
        var store = new RecordingInboxStore { HasProcessed = true };
        var inner = new RecordingHandler();
        var observer = new RecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var sut = new InboxMiddleware(inner, store, notifier);
        var context = CreateContext("message-1");

        await sut.Handle(context);

        Assert.AreEqual(0, inner.HandleCalls);
        Assert.AreEqual(0, store.RecordCalls);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, context.HandlerOutcome);
        Assert.AreEqual(1, observer.Duplicates.Count);
        Assert.AreEqual("message-1", observer.Duplicates[0].MessageId);
        Assert.AreEqual("event-1", observer.Duplicates[0].EventId);
        Assert.AreEqual("billing", observer.Duplicates[0].EndpointId);
        Assert.AreEqual("customer-1", observer.Duplicates[0].SessionId);
    }

    [TestMethod]
    public async Task Handler_failure_is_not_recorded()
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler
        {
            Exception = new TransientException("handler failed"),
        };
        var sut = new InboxMiddleware(inner, store);

        await Assert.ThrowsExactlyAsync<TransientException>(
            () => sut.Handle(CreateContext("message-1")));

        Assert.AreEqual(1, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
    }

    [TestMethod]
    public async Task Permanent_handler_failure_is_not_recorded()
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler
        {
            Exception = new PermanentFailureException(new FormatException("invalid payload")),
        };
        var sut = new InboxMiddleware(inner, store);

        await Assert.ThrowsExactlyAsync<PermanentFailureException>(
            () => sut.Handle(CreateContext("message-1")));

        Assert.AreEqual(1, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
    }

    [TestMethod]
    public async Task Store_check_failure_becomes_sanitized_transient_failure()
    {
        var store = new RecordingInboxStore
        {
            CheckException = new InvalidOperationException("server=secret; password=secret"),
        };
        var inner = new RecordingHandler();
        var sut = new InboxMiddleware(inner, store);

        var exception = await Assert.ThrowsExactlyAsync<InboxStoreException>(
            () => sut.Handle(CreateContext("message-1")));

        Assert.AreEqual(InboxStoreException.SafeMessage, exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, inner.HandleCalls);
    }

    [TestMethod]
    public async Task Store_failure_is_abandoned_by_base_handler_before_settlement()
    {
        var store = new RecordingInboxStore
        {
            CheckException = new InvalidOperationException("provider details"),
        };
        var inner = new RecordingHandler();
        var middleware = new InboxMiddleware(inner, store);
        var sut = new StrictMessageHandler(
            middleware,
            new ResponseService(new InMemoryMessageBus()));
        var context = CreateContext("message-1");

        await sut.Handle(context);

        Assert.IsTrue(context.IsAbandoned);
        Assert.IsFalse(context.IsCompleted);
        Assert.IsFalse(context.IsDeadLettered);
        Assert.AreEqual(0, inner.HandleCalls);
    }

    [TestMethod]
    public async Task Store_record_failure_occurs_after_handler_and_becomes_transient()
    {
        var order = new List<string>();
        var store = new RecordingInboxStore(order)
        {
            RecordException = new InvalidOperationException("provider details"),
        };
        var inner = new RecordingHandler(order);
        var sut = new InboxMiddleware(inner, store);

        await Assert.ThrowsExactlyAsync<InboxStoreException>(
            () => sut.Handle(CreateContext("message-1")));

        CollectionAssert.AreEqual(
            SuccessfulProcessingOrder,
            order);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task Missing_or_blank_message_id_falls_through_without_store(string messageId)
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler();
        var sut = new InboxMiddleware(inner, store);

        await sut.Handle(CreateContext(messageId));

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(0, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task Missing_or_blank_endpoint_id_falls_through_without_store(string endpointId)
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler();
        var sut = new InboxMiddleware(inner, store);

        await sut.Handle(CreateContext("message-1", endpointId));

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(0, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
    }

    [TestMethod]
    public async Task Message_id_longer_than_512_falls_through_without_store()
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler();
        var sut = new InboxMiddleware(inner, store);

        await sut.Handle(CreateContext(new string('x', InboxMiddleware.MaximumMessageIdLength + 1)));

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(0, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
    }

    [TestMethod]
    public async Task Message_id_at_512_character_boundary_uses_store()
    {
        var store = new RecordingInboxStore();
        var inner = new RecordingHandler();
        var sut = new InboxMiddleware(inner, store);

        await sut.Handle(CreateContext(new string('x', InboxMiddleware.MaximumMessageIdLength)));

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(1, store.CheckCalls);
        Assert.AreEqual(1, store.RecordCalls);
    }

    [TestMethod]
    public async Task Duplicate_lifecycle_observer_failure_is_best_effort()
    {
        var store = new RecordingInboxStore { HasProcessed = true };
        var inner = new RecordingHandler();
        var notifier = new MessageLifecycleNotifier([new ThrowingObserver()]);
        var sut = new InboxMiddleware(inner, store, notifier);
        var context = CreateContext("message-1");

        await sut.Handle(context);

        Assert.AreEqual(HandlerOutcome.DuplicateDetected, context.HandlerOutcome);
        Assert.AreEqual(0, inner.HandleCalls);
    }

    [TestMethod]
    public async Task Caller_cancellation_from_store_is_not_wrapped()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new RecordingInboxStore
        {
            CheckException = new OperationCanceledException(cancellation.Token),
        };
        var sut = new InboxMiddleware(new RecordingHandler(), store);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.Handle(CreateContext("message-1"), cancellation.Token));
    }

    [TestMethod]
    public async Task Inbox_metrics_use_only_bounded_operation_tags()
    {
        using var capture = new InboxMetricCapture();

        var duplicateStore = new RecordingInboxStore { HasProcessed = true };
        await new InboxMiddleware(new RecordingHandler(), duplicateStore)
            .Handle(CreateContext("sensitive-message-id"));

        var failingStore = new RecordingInboxStore
        {
            CheckException = new InvalidOperationException("failure"),
        };
        await Assert.ThrowsExactlyAsync<InboxStoreException>(() =>
            new InboxMiddleware(new RecordingHandler(), failingStore)
                .Handle(CreateContext("another-sensitive-id")));

        var duplicate = capture.Measurements.Single(m =>
            m.Name == "nimbus.inbox.duplicate_detected");
        Assert.AreEqual(0, duplicate.Tags.Count);

        var failure = capture.Measurements.Single(m =>
            m.Name == "nimbus.inbox.operation.failed");
        Assert.AreEqual(1, failure.Tags.Count);
        Assert.AreEqual("check", failure.Tags[MessagingAttributes.NimBusStoreOperation]);
        Assert.IsFalse(failure.Tags.Keys.Any(key =>
            key.Contains("message", StringComparison.OrdinalIgnoreCase)
            || key.Contains("session", StringComparison.OrdinalIgnoreCase)
            || key.Contains("event", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Inbox_options_have_safe_defaults_and_require_explicit_provider()
    {
        var options = new InboxOptions();

        Assert.AreEqual(TimeSpan.FromDays(7), options.RetentionPeriod);
        Assert.AreEqual(TimeSpan.FromHours(1), options.CleanupInterval);
        Assert.IsNull(options.DeduplicationStore);
    }

    [TestMethod]
    public async Task Test_transport_honors_UseInbox_without_Azure_Service_Bus()
    {
        var handlerCalls = 0;
        var services = new ServiceCollection();
        services.AddNimBusInMemoryInbox();
        services.AddNimBusTestTransport(builder =>
        {
            builder.AddDynamicHandler(
                "OrderPlaced",
                () => new DelegateEventJsonHandler((_, _) =>
                {
                    handlerCalls++;
                    return Task.CompletedTask;
                }));
            builder.UseInbox(options => options.DeduplicationStore = InboxStore.InMemory);
        });

        using var provider = services.BuildServiceProvider();
        var messageHandler = provider.GetRequiredService<IMessageHandler>();
        var first = CreateContext("message-1");
        var duplicate = CreateContext("message-1");

        await messageHandler.Handle(first);
        await messageHandler.Handle(duplicate);

        Assert.AreEqual(1, handlerCalls);
        Assert.IsTrue(first.IsCompleted);
        Assert.IsTrue(duplicate.IsCompleted);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, duplicate.HandlerOutcome);
    }

    private static InMemoryMessageContext CreateContext(string messageId, string endpointId = "billing")
    {
        return new InMemoryMessageContext(
            new Message
            {
                MessageId = messageId,
                EventId = "event-1",
                EventTypeId = "OrderPlaced",
                To = endpointId,
                SessionId = "customer-1",
                CorrelationId = "correlation-1",
                MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = "OrderPlaced",
                        EventJson = "{}",
                    },
                },
                From = "sales",
                OriginatingFrom = "sales",
                ParentMessageId = Constants.Self,
                OriginatingMessageId = Constants.Self,
            },
            new InMemorySessionState());
    }

    private sealed class RecordingInboxStore : IInboxStore
    {
        private readonly List<string>? _order;

        public RecordingInboxStore(List<string>? order = null)
        {
            _order = order;
        }

        private readonly HashSet<(string EndpointId, string MessageId)> _recorded = [];

        public bool HasProcessed { get; set; }
        public bool TrackRecords { get; set; }
        public Exception? CheckException { get; set; }
        public Exception? RecordException { get; set; }
        public int CheckCalls { get; private set; }
        public int RecordCalls { get; private set; }
        public string? LastCheckedEndpointId { get; private set; }
        public string? LastCheckedMessageId { get; private set; }
        public string? LastRecordedEndpointId { get; private set; }
        public string? LastRecordedMessageId { get; private set; }

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            LastCheckedEndpointId = endpointId;
            LastCheckedMessageId = messageId;
            _order?.Add("check");
            if (CheckException is not null)
                throw CheckException;
            return Task.FromResult(HasProcessed || (TrackRecords && _recorded.Contains((endpointId, messageId))));
        }

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            LastRecordedEndpointId = endpointId;
            LastRecordedMessageId = messageId;
            _order?.Add("record");
            if (RecordException is not null)
                throw RecordException;
            _recorded.Add((endpointId, messageId));
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingHandler : IEventContextHandler
    {
        private readonly List<string>? _order;

        public RecordingHandler(List<string>? order = null)
        {
            _order = order;
        }

        public int HandleCalls { get; private set; }
        public Exception? Exception { get; set; }
        public Action<IMessageContext>? OnHandle { get; set; }

        public Task Handle(
            IMessageContext context,
            CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            _order?.Add("handler-start");
            OnHandle?.Invoke(context);
            if (Exception is not null)
                throw Exception;
            _order?.Add("handler-end");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingObserver : IMessageLifecycleObserver
    {
        public List<MessageLifecycleContext> Duplicates { get; } = [];

        public Task OnDuplicateDetected(
            MessageLifecycleContext context,
            CancellationToken cancellationToken = default)
        {
            Duplicates.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IMessageLifecycleObserver
    {
        public Task OnDuplicateDetected(
            MessageLifecycleContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("observer failed");
    }

    private sealed class InboxMetricCapture : IDisposable
    {
        private readonly MeterListener _listener;

        public InboxMetricCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == NimBusInstrumentation.ConsumerMeterName
                        && instrument.Name.StartsWith("nimbus.inbox.", StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var capturedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    capturedTags[tag.Key] = tag.Value;
                }

                Measurements.Add(new InboxMeasurement(instrument.Name, value, capturedTags));
            });
            _listener.Start();
        }

        public List<InboxMeasurement> Measurements { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed record InboxMeasurement(
        string Name,
        long Value,
        IReadOnlyDictionary<string, object?> Tags);
}
