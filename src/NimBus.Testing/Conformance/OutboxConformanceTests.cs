using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IOutbox"/> dispatch checkpoints.
/// </summary>
[TestClass]
public abstract class OutboxConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// Creates an isolated outbox for the current test.
    /// </summary>
    /// <returns>The outbox under test.</returns>
    protected abstract Task<IOutbox> CreateOutboxAsync();

    /// <summary>
    /// Reads the persisted dispatch timestamp, including for rows that are no longer pending.
    /// </summary>
    /// <param name="id">The outbox row identifier.</param>
    /// <returns>The persisted dispatch timestamp, or <c>null</c> when it has not been dispatched.</returns>
    protected abstract Task<DateTime?> GetDispatchedAtUtcAsync(string id);

    /// <summary>
    /// Advances time enough that replacing a persisted dispatch timestamp is observable.
    /// Deterministic providers should override this method to advance their test clock.
    /// </summary>
    /// <returns>A task that completes after the provider clock advances.</returns>
    protected virtual Task AdvanceDispatchClockAsync() => Task.Delay(TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// Verifies that repeatedly checkpointing one row preserves its first dispatch timestamp.
    /// </summary>
    [TestMethod]
    public async Task MarkAsDispatchedAsync_single_is_idempotent_and_excludes_the_row_from_pending()
    {
        var outbox = await CreateOutboxAsync();
        var dispatched = CreateMessage("single-dispatched", 0);
        var stillPending = CreateMessage("single-pending", 1);
        await outbox.StoreAsync(dispatched);
        await outbox.StoreAsync(stillPending);

        await outbox.MarkAsDispatchedAsync(dispatched.Id);
        var firstTimestamp = await GetDispatchedAtUtcAsync(dispatched.Id);
        Assert.IsNotNull(firstTimestamp);

        await AdvanceDispatchClockAsync();
        await outbox.MarkAsDispatchedAsync(dispatched.Id);

        Assert.AreEqual(firstTimestamp, await GetDispatchedAtUtcAsync(dispatched.Id));
        CollectionAssert.AreEqual(
            new[] { stillPending.Id },
            (await outbox.GetPendingAsync(10)).Select(message => message.Id).ToArray());
    }

    /// <summary>
    /// Verifies that overlapping and repeated batch checkpoints preserve every first timestamp.
    /// </summary>
    [TestMethod]
    public async Task MarkAsDispatchedAsync_batch_is_idempotent_after_partial_checkpoint()
    {
        var outbox = await CreateOutboxAsync();
        var first = CreateMessage("batch-first", 0);
        var second = CreateMessage("batch-second", 1);
        var third = CreateMessage("batch-third", 2);
        await outbox.StoreBatchAsync([first, second, third]);

        await outbox.MarkAsDispatchedAsync(new[] { first.Id, second.Id });
        var firstTimestamps = await GetTimestampsAsync(first.Id, second.Id);
        Assert.IsTrue(firstTimestamps.Values.All(timestamp => timestamp.HasValue));
        CollectionAssert.AreEqual(
            new[] { third.Id },
            (await outbox.GetPendingAsync(10)).Select(message => message.Id).ToArray());

        await AdvanceDispatchClockAsync();
        await outbox.MarkAsDispatchedAsync(new[] { first.Id, second.Id, third.Id });
        var overlappingTimestamps = await GetTimestampsAsync(first.Id, second.Id, third.Id);
        Assert.AreEqual(firstTimestamps[first.Id], overlappingTimestamps[first.Id]);
        Assert.AreEqual(firstTimestamps[second.Id], overlappingTimestamps[second.Id]);
        Assert.IsNotNull(overlappingTimestamps[third.Id]);

        await AdvanceDispatchClockAsync();
        await outbox.MarkAsDispatchedAsync(new[] { first.Id, second.Id, third.Id });
        var repeatedTimestamps = await GetTimestampsAsync(first.Id, second.Id, third.Id);

        CollectionAssert.AreEquivalent(overlappingTimestamps.Keys, repeatedTimestamps.Keys);
        foreach (var id in overlappingTimestamps.Keys)
        {
            Assert.AreEqual(overlappingTimestamps[id], repeatedTimestamps[id], $"Dispatch timestamp changed for {id}.");
        }

        Assert.AreEqual(0, (await outbox.GetPendingAsync(10)).Count);
    }

    private OutboxMessage CreateMessage(string suffix, int order) => new()
    {
        Id = $"{_scope}-{suffix}",
        MessageId = $"{_scope}-message-{order}",
        To = "conformance-endpoint",
        EventTypeId = "ConformanceEvent",
        SessionId = $"session-{order}",
        CorrelationId = $"correlation-{order}",
        Payload = $"{{\"order\":{order}}}",
        CreatedAtUtc = new DateTime(2030, 1, 1, 0, 0, order, DateTimeKind.Utc),
    };

    private async Task<Dictionary<string, DateTime?>> GetTimestampsAsync(params string[] ids)
    {
        var timestamps = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            timestamps.Add(id, await GetDispatchedAtUtcAsync(id));
        }

        return timestamps;
    }
}
