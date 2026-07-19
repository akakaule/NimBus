#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Outbox;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK;
using NimBus.Testing;

namespace NimBus.EndToEnd.Tests;

[TestClass]
public sealed class InboxEndToEndTests
{
    [TestMethod]
    public async Task Outbox_redelivery_after_success_is_skipped_and_reported()
    {
        var store = new InMemoryInboxStore();
        var observer = new DuplicateRecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var fixture = EndToEndFixture.CreateWithHandlerDecorator(
            inner => new InboxMiddleware(inner, store, notifier),
            notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var outbox = new ReplayableOutbox();
        var publisher = new PublisherClient(new OutboxSender(outbox));
        var dispatcher = new OutboxDispatcher(outbox, fixture.PublishBus);
        await publisher.Publish(
            new OrderPlaced("inbox-success-session") { OrderId = "ORD-INBOX-1" },
            "inbox-success-session",
            "inbox-success-correlation",
            "inbox-success-message");

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        var firstDelivery = await fixture.DeliverAllWithResults();
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        Assert.IsTrue(firstDelivery.Single().Session.WasCompleted);
        Assert.IsTrue(await store.HasProcessedAsync("inbox-success-message"));

        // Simulate the publish-side crash window: the outbox send succeeded but its
        // checkpoint did not, so the same stored message is dispatched again.
        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        var duplicateDelivery = await fixture.DeliverAllWithResults();

        Assert.AreEqual(1, handler.ReceivedEvents.Count, "The duplicate must not reach the application handler.");
        Assert.IsTrue(duplicateDelivery.Single().Session.WasCompleted, "A duplicate must be settled normally.");
        Assert.AreEqual(1, observer.Duplicates.Count);
        Assert.AreEqual("inbox-success-message", observer.Duplicates[0].MessageId);
        Assert.AreEqual(duplicateDelivery.Single().Context.To, observer.Duplicates[0].EndpointId);
        Assert.AreEqual(duplicateDelivery.Single().Context.EventId, observer.Duplicates[0].EventId);
        Assert.AreEqual(duplicateDelivery.Single().Context.SessionId, observer.Duplicates[0].SessionId);

        var duplicateResponse = fixture.ResponseBus.SentMessages.Single(
            message => message.MessageType == MessageType.SkipResponse);
        Assert.AreEqual(
            InboxMiddleware.DuplicateReason,
            duplicateResponse.MessageContent.ErrorContent.ErrorText);
    }

    [TestMethod]
    public async Task Failed_first_attempt_is_not_recorded_and_redelivery_runs_again()
    {
        var store = new InMemoryInboxStore();
        var observer = new DuplicateRecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var fixture = EndToEndFixture.CreateWithHandlerDecorator(
            inner => new InboxMiddleware(inner, store, notifier),
            notifier);

        var attempts = 0;
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = _ => Interlocked.Increment(ref attempts) == 1
                ? new TransientException("first attempt fails")
                : null,
        };
        fixture.RegisterHandler(() => handler);

        var outbox = new ReplayableOutbox();
        var publisher = new PublisherClient(new OutboxSender(outbox));
        var dispatcher = new OutboxDispatcher(outbox, fixture.PublishBus);
        await publisher.Publish(
            new OrderPlaced("inbox-retry-session") { OrderId = "ORD-INBOX-2" },
            "inbox-retry-session",
            "inbox-retry-correlation",
            "inbox-retry-message");

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(await store.HasProcessedAsync("inbox-retry-message"));

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(2, attempts, "The unrecorded redelivery must run the handler again.");
        Assert.IsTrue(await store.HasProcessedAsync("inbox-retry-message"));

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(2, attempts, "A later redelivery must be skipped after the successful attempt.");
        Assert.AreEqual(1, observer.Duplicates.Count);
    }

    private sealed class DuplicateRecordingObserver : IMessageLifecycleObserver
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

    private sealed class ReplayableOutbox : IOutbox
    {
        private readonly List<OutboxMessage> _messages = [];

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreBatchAsync(
            IEnumerable<OutboxMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(_messages.Take(batchSize).ToList());
        }

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MarkAsDispatchedAsync(
            IEnumerable<string> ids,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
