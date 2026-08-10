#pragma warning disable CA1707, CA2007

using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Storage;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class TopologyJournalTests
{
    [TestMethod]
    public async Task Journal_round_trips_topology_without_messages()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        var source = new BrokerNamespace(new BrokerOptions());
        source.CreateTopic(new TopicDefinition("events"));
        source.CreateSubscription("events", new SubscriptionDefinition("consumer") { RequiresSession = true });
        source.DeleteRule("events", "consumer", "$Default");
        source.CreateRule("events", "consumer", new RuleDefinition("orders", "user.EventTypeId = 'order'"));
        source.Publish("events", new BrokerMessage { MessageId = "volatile" });

        using (var journal = new TopologyJournal(path))
        {
            await journal.SaveAsync(source, CancellationToken.None);
        }

        var restored = new BrokerNamespace(new BrokerOptions());
        using (var journal = new TopologyJournal(path))
        {
            await journal.ReplayAsync(restored, CancellationToken.None);
        }

        Assert.IsTrue(restored.TopicExists("events"));
        Assert.IsTrue(restored.GetSubscriptionDefinition("events", "consumer").RequiresSession);
        Assert.AreEqual("user.EventTypeId = 'order'", restored.GetRule("events", "consumer", "orders").FilterExpression);
        Assert.AreEqual(0, restored.GetSubscriptionRuntimeProperties("events", "consumer").TotalMessageCount);
    }

    [TestMethod]
    public async Task Corrupt_journal_is_renamed_and_broker_starts_empty()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "topology.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-json");
        var broker = new BrokerNamespace(new BrokerOptions());

        using var journal = new TopologyJournal(path);
        await journal.ReplayAsync(broker, CancellationToken.None);

        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "topology.json.corrupt-*"));
        Assert.HasCount(0, broker.GetTopics());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nimbus-journal-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
