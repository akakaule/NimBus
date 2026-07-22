#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CloudEvents;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using NimBus.Testing;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Proves the inbox deduplication scope for external CloudEvents on the real Service Bus
/// <see cref="MessageContext"/>. Such a producer stamps no <c>user.To</c>, so the transport
/// substitutes the mapped event type — the configured subscriber endpoint, passed as the
/// detector/middleware scope, must key the store instead: the event type is neither unique
/// per endpoint nor distinct across endpoints sharing one physical store.
/// </summary>
[TestClass]
public class InboxCloudEventsScopeTests
{
    [TestMethod]
    public async Task Detector_keys_the_check_by_the_configured_endpoint_not_the_mapped_type()
    {
        var store = new RecordingInboxStore();
        var detector = new InboxDuplicateDetector(store, endpointScope: "Billing");
        var context = CreateCloudEventContext("OrderPlaced", "ext-1");
        Assert.AreEqual("OrderPlaced", context.To, "Precondition: an external CloudEvent's To is only the mapped type");

        Assert.IsFalse(await detector.IsDuplicateAsync(context));

        Assert.AreEqual("Billing", store.LastCheckedEndpointId);
        Assert.AreEqual("ext-1", store.LastCheckedMessageId);
    }

    [TestMethod]
    public async Task Same_type_deliveries_to_two_endpoints_sharing_a_store_do_not_cross_skip()
    {
        // Fan-out delivers the same CloudEvent (same id, same type) to both subscribers. With
        // the event type as the scope both would collide on one key, so the second endpoint's
        // legitimate first delivery would be skipped.
        var store = new InMemoryInboxStore();
        var billingDetector = new InboxDuplicateDetector(store, endpointScope: "Billing");
        var shippingDetector = new InboxDuplicateDetector(store, endpointScope: "Shipping");
        await store.RecordProcessedAsync("Billing", "ext-1");

        Assert.IsTrue(
            await billingDetector.IsDuplicateAsync(CreateCloudEventContext("OrderPlaced", "ext-1")),
            "The recording endpoint must see its redelivery as a duplicate");
        Assert.IsFalse(
            await shippingDetector.IsDuplicateAsync(CreateCloudEventContext("OrderPlaced", "ext-1")),
            "Another endpoint's first delivery must not be skipped by the recording endpoint's record");
    }

    [TestMethod]
    public async Task Reused_message_id_across_event_types_on_one_endpoint_is_deduplicated()
    {
        // The contract keys on (endpoint, message id). With the event type as the scope, a
        // producer reusing one message id across two types on the same endpoint would be
        // processed twice.
        var store = new InMemoryInboxStore();
        var inner = new CountingHandler();
        var middleware = new InboxMiddleware(inner, store, endpointScope: "Billing");

        await middleware.Handle(CreateCloudEventContext("OrderPlaced", "ext-1"));
        Assert.AreEqual(1, inner.HandleCalls);

        var reusedIdContext = CreateCloudEventContext("InvoicePaid", "ext-1");
        await middleware.Handle(reusedIdContext);

        Assert.AreEqual(1, inner.HandleCalls, "The reused message id must be skipped regardless of its event type");
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, reusedIdContext.HandlerOutcome);
    }

    private static MessageContext CreateCloudEventContext(string eventType, string cloudEventId)
    {
        var message = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"orderId\":\"O-1\"}"),
        };
        message.Properties["cloudEvents:specversion"] = "1.0";
        message.Properties["cloudEvents:id"] = cloudEventId;
        message.Properties["cloudEvents:source"] = "urn:ext:crm";
        message.Properties["cloudEvents:type"] = eventType;

        return new MessageContext(
            message,
            new InertServiceBusSession(),
            isDeferred: false,
            new CloudEventReadOptions());
    }

    private sealed class RecordingInboxStore : IInboxStore
    {
        public string? LastCheckedEndpointId { get; private set; }
        public string? LastCheckedMessageId { get; private set; }

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            LastCheckedEndpointId = endpointId;
            LastCheckedMessageId = messageId;
            return Task.FromResult(false);
        }

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class CountingHandler : IEventContextHandler
    {
        public int HandleCalls { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }
}
