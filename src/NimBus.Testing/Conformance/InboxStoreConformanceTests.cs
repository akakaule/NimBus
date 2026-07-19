using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic behavioral tests for <see cref="IInboxStore"/> implementations.
/// </summary>
[TestClass]
public abstract class InboxStoreConformanceTests
{
    private readonly string _scope = $"inbox-ct-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates an inbox store for the current test.
    /// </summary>
    /// <returns>The isolated store under test.</returns>
    protected abstract Task<IInboxStore> CreateStoreAsync();

    /// <summary>
    /// Advances the provider clock past the first record and returns a cutoff between
    /// the first and second records. Providers backed by a controllable clock should
    /// override this hook rather than waiting in real time.
    /// </summary>
    /// <returns>A cutoff later than the first persisted timestamp.</returns>
    protected virtual async Task<DateTimeOffset> AdvancePastFirstRecordAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Verifies the basic not-seen, record, seen transition.
    /// </summary>
    [TestMethod]
    public async Task HasProcessedAsync_transitions_from_false_to_true_after_record()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("transition");

        Assert.IsFalse(await store.HasProcessedAsync(messageId));

        await store.RecordProcessedAsync(messageId);

        Assert.IsTrue(await store.HasProcessedAsync(messageId));
    }

    /// <summary>
    /// Verifies repeated and concurrent records are idempotent.
    /// </summary>
    [TestMethod]
    public async Task RecordProcessedAsync_is_idempotent_when_repeated_concurrently()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("concurrent");

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => store.RecordProcessedAsync(messageId)));
        await store.RecordProcessedAsync(messageId);

        Assert.IsTrue(await store.HasProcessedAsync(messageId));
    }

    /// <summary>
    /// Verifies message identifiers are opaque, case-sensitive keys across providers.
    /// </summary>
    [TestMethod]
    public async Task Message_identifiers_are_case_sensitive()
    {
        var store = await CreateStoreAsync();
        var upperCaseMessageId = NewMessageId("Case-Sensitive");
        var lowerCaseMessageId = upperCaseMessageId.ToLowerInvariant();

        await store.RecordProcessedAsync(upperCaseMessageId);

        Assert.IsTrue(await store.HasProcessedAsync(upperCaseMessageId));
        Assert.IsFalse(await store.HasProcessedAsync(lowerCaseMessageId));

        await store.RecordProcessedAsync(lowerCaseMessageId);

        Assert.IsTrue(await store.HasProcessedAsync(lowerCaseMessageId));
    }

    /// <summary>
    /// Verifies cleanup removes only expired rows, preserves the original timestamp
    /// after repeated records, and is a no-op when no expired rows remain.
    /// </summary>
    [TestMethod]
    public async Task PurgeExpiredAsync_removes_expired_rows_and_retains_newer_rows()
    {
        var store = await CreateStoreAsync();
        var expiredMessageId = NewMessageId("expired");
        var retainedMessageId = NewMessageId("retained");

        await store.RecordProcessedAsync(expiredMessageId);
        var cutoff = await AdvancePastFirstRecordAsync();

        // A repeated record must not replace the first timestamp. If it did, the
        // expired row would incorrectly survive this purge.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.RecordProcessedAsync(expiredMessageId)));
        await store.RecordProcessedAsync(retainedMessageId);

        Assert.AreEqual(1, await store.PurgeExpiredAsync(cutoff));
        Assert.IsFalse(await store.HasProcessedAsync(expiredMessageId));
        Assert.IsTrue(await store.HasProcessedAsync(retainedMessageId));
        Assert.AreEqual(0, await store.PurgeExpiredAsync(cutoff));
    }

    /// <summary>
    /// Verifies pre-cancelled operations fail before reading or mutating state.
    /// </summary>
    [TestMethod]
    public async Task Operations_honor_pre_cancelled_tokens_without_recording()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.HasProcessedAsync(messageId, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.RecordProcessedAsync(messageId, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.PurgeExpiredAsync(DateTimeOffset.MaxValue, cancellation.Token));

        Assert.IsFalse(await store.HasProcessedAsync(messageId));
    }

    private string NewMessageId(string suffix) => $"{_scope}-{suffix}";
}
