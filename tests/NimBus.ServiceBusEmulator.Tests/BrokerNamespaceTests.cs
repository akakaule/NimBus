#pragma warning disable CA1707, CA2007

using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class BrokerNamespaceTests
{
    [TestMethod]
    public void Publish_rejects_before_exceeding_the_broker_memory_budget()
    {
        var broker = new BrokerNamespace(new BrokerOptions { MaxStoredBytes = 3 });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));

        Assert.ThrowsExactly<BrokerQuotaExceededException>(() =>
            broker.Publish("events", new BrokerMessage { Body = new byte[4] }));
        Assert.AreEqual(0, broker.GetSubscriptionRuntimeProperties("events", "consumer").TotalMessageCount);
    }

    [TestMethod]
    public void Publish_applies_rules_per_subscription_and_preserves_guid_type()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));
        broker.ReplaceRule(
            "events",
            "consumer",
            new RuleDefinition(
                "route",
                "user.EventTypeId = 'order-created' AND user.From IS NULL",
                "SET user.From = 'orders'; SET user.EventId = newid(); SET user.To = 'consumer';"));

        var message = new BrokerMessage
        {
            MessageId = "message-1",
            Body = System.Text.Encoding.UTF8.GetBytes("payload"),
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventTypeId"] = "order-created",
            },
        };

        broker.Publish("events", message);

        var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(delivery);
        Assert.AreEqual("orders", delivery.Message.ApplicationProperties["From"]);
        Assert.AreEqual("consumer", delivery.Message.ApplicationProperties["To"]);
        Assert.IsInstanceOfType<Guid>(delivery.Message.ApplicationProperties["EventId"]);
    }

    [TestMethod]
    public void Max_delivery_count_moves_message_to_dead_letter_queue_without_extra_delivery()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { MaxDeliveryCount = 3 });
        broker.Publish("events", NewMessage("message-1"));

        for (var expected = 1; expected <= 3; expected++)
        {
            var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
            Assert.IsNotNull(delivery);
            Assert.AreEqual(expected, delivery.Message.DeliveryCount);
            broker.Abandon("events", "consumer", delivery.LockToken, "receiver-1");
        }

        Assert.IsNull(broker.TryAcquire("events", "consumer", null, "receiver-1"));
        var properties = broker.GetSubscriptionRuntimeProperties("events", "consumer");
        Assert.AreEqual(0L, properties.ActiveMessageCount);
        Assert.AreEqual(1L, properties.DeadLetterMessageCount);
    }

    [TestMethod]
    public void Session_is_exclusive_and_clean_release_does_not_increment_delivery_count()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });
        broker.Publish("events", NewMessage("message-1", "session-1"));

        var session = broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(session);
        Assert.ThrowsExactly<SessionCannotBeLockedException>(() =>
            broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2"));

        var first = broker.TryAcquire("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.Message.DeliveryCount);

        broker.ReleaseSession("events", "consumer", "session-1", "receiver-1");
        var reacquired = broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(reacquired);
        var second = broker.TryAcquire("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(second);
        Assert.AreEqual(1, second.Message.DeliveryCount);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("abandon")]
    [DataRow("dead-letter")]
    [DataRow("release")]
    [DataRow("complete-by-token")]
    [DataRow("abandon-by-token")]
    [DataRow("dead-letter-by-token")]
    public void Expired_message_lock_cannot_be_settled(string operation)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { LockDuration = TimeSpan.FromSeconds(1) });
        broker.Publish("events", NewMessage("message-1"));

        var expired = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(expired);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<KeyNotFoundException>(() => SettleExpired(operation, broker, expired.LockToken));
        var redelivered = broker.TryAcquire("events", "consumer", null, "receiver-2");
        Assert.IsNotNull(redelivered);
        Assert.AreEqual(2, redelivered.Message.DeliveryCount);
    }

    [TestMethod]
    public void Session_management_requires_the_current_lock_owner()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });
        broker.Publish("events", NewMessage("message-1", "session-1"));
        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1"));

        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-2"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.GetSessionState("events", "consumer", "session-1", "receiver-2"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.SetSessionState("events", "consumer", "session-1", "receiver-2", new byte[] { 1 }));

        broker.SetSessionState("events", "consumer", "session-1", "receiver-1", new byte[] { 1 });
        Assert.AreEqual(1, broker.GetSessionState("events", "consumer", "session-1", "receiver-1")?.Length);
        Assert.IsGreaterThan(
            DateTimeOffset.UtcNow,
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-1"));
    }

    [TestMethod]
    public void Expired_session_lock_cannot_be_completed_or_managed()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer")
            {
                RequiresSession = true,
                LockDuration = TimeSpan.FromSeconds(1),
            });
        broker.Publish("events", NewMessage("message-1", "session-1"));
        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1"));
        var expired = broker.TryAcquire("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(expired);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.Complete("events", "consumer", expired.LockToken, "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.GetSessionState("events", "consumer", "session-1", "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.SetSessionState("events", "consumer", "session-1", "receiver-1", new byte[] { 1 }));

        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2"));
        var redelivered = broker.TryAcquire("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(redelivered);
        Assert.AreEqual(2, redelivered.Message.DeliveryCount);
    }

    [TestMethod]
    public void Scheduled_message_starts_ttl_at_activation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));

        var message = NewMessage("message-1") with
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            ScheduledEnqueueTime = clock.GetUtcNow().AddHours(1),
        };
        broker.Publish("events", message);

        clock.Advance(TimeSpan.FromMinutes(59));
        broker.ProcessDueWork();
        Assert.IsNull(broker.TryAcquire("events", "consumer", null, "receiver-1"));

        clock.Advance(TimeSpan.FromMinutes(1));
        broker.ProcessDueWork();
        var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(delivery);
        Assert.AreEqual(clock.GetUtcNow(), delivery.Message.EnqueuedTime);
    }

    private static BrokerMessage NewMessage(string messageId, string? sessionId = null) => new()
    {
        MessageId = messageId,
        SessionId = sessionId,
        Body = System.Text.Encoding.UTF8.GetBytes("payload"),
    };

    private static void SettleExpired(string operation, BrokerNamespace broker, Guid lockToken)
    {
        switch (operation)
        {
            case "complete":
                broker.Complete("events", "consumer", lockToken, "receiver-1");
                break;
            case "abandon":
                broker.Abandon("events", "consumer", lockToken, "receiver-1");
                break;
            case "dead-letter":
                broker.DeadLetter("events", "consumer", lockToken, "receiver-1", null, null);
                break;
            case "release":
                broker.Release("events", "consumer", lockToken, "receiver-1");
                break;
            case "complete-by-token":
                broker.CompleteByLockToken("events", "consumer", lockToken);
                break;
            case "abandon-by-token":
                broker.AbandonByLockToken("events", "consumer", lockToken);
                break;
            case "dead-letter-by-token":
                broker.DeadLetterByLockToken("events", "consumer", lockToken, null, null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown settlement operation.");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
