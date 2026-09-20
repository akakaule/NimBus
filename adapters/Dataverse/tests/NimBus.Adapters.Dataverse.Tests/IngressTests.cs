#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK;

namespace NimBus.Adapters.Dataverse.Tests;

[TestClass]
public sealed class IngressTests
{
    [TestMethod]
    public async Task Publication_precedes_completion_and_redelivery_preserves_identity()
    {
        var sender = new RecordingSender();
        var ingress = Create(sender);
        var completions = 0;
        Task Complete(CancellationToken _) { Assert.AreEqual(completions + 1, sender.Messages.Count); completions++; return Task.CompletedTask; }
        await ingress.ProcessAsync(ContextReaderTests.Context().ToString(), false, Complete, UnexpectedDeadLetter);
        await ingress.ProcessAsync(ContextReaderTests.Context().ToString(), false, Complete, UnexpectedDeadLetter);
        Assert.AreEqual(2, completions);
        Assert.AreEqual(sender.Messages[0].MessageId, sender.Messages[1].MessageId);
        Assert.AreEqual(sender.Messages[0].MessageContent.EventContent.EventJson, sender.Messages[1].MessageContent.EventContent.EventJson);
        Assert.AreEqual("DataverseRecordUpdatedV1", sender.Messages[0].EventTypeId);
        Assert.AreEqual("DataverseEndpoint", sender.Messages[0].OriginatingFrom);
        var different = ContextReaderTests.Context();
        different["OperationId"] = Guid.NewGuid();
        await ingress.ProcessAsync(different.ToString(), false, Complete, UnexpectedDeadLetter);
        Assert.AreNotEqual(sender.Messages[0].MessageId, sender.Messages[2].MessageId);
        Assert.AreEqual(sender.Messages[0].SessionId, sender.Messages[2].SessionId);
    }

    [TestMethod]
    public async Task Send_failure_does_not_complete_or_deadletter()
    {
        var sender = new RecordingSender { Fail = true };
        await Assert.ThrowsExactlyAsync<IOException>(() => Create(sender).ProcessAsync(
            ContextReaderTests.Context().ToString(), false, UnexpectedComplete, UnexpectedDeadLetter));
    }

    [TestMethod]
    public async Task Invalid_source_is_deadlettered_without_publication()
    {
        var sender = new RecordingSender();
        string? reason = null;
        await Create(sender).ProcessAsync("{", false, UnexpectedComplete, (value, _) => { reason = value; return Task.CompletedTask; });
        Assert.AreEqual("InvalidJson", reason);
        Assert.AreEqual(0, sender.Messages.Count);
    }

    [TestMethod]
    public async Task Completion_failure_and_cancellation_propagate()
    {
        var sender = new RecordingSender();
        await Assert.ThrowsExactlyAsync<IOException>(() => Create(sender).ProcessAsync(ContextReaderTests.Context().ToString(),
            false, _ => throw new IOException("lost lock"), UnexpectedDeadLetter));
        Assert.AreEqual(1, sender.Messages.Count);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Create(sender).ProcessAsync(
            ContextReaderTests.Context().ToString(), false, UnexpectedComplete, UnexpectedDeadLetter, cancelled.Token));
        Assert.AreEqual(1, sender.Messages.Count);
    }

    private static DataverseIngress Create(RecordingSender sender)
    {
        var options = ContextReaderTests.Options();
        return new DataverseIngress(new DataverseContextReader(options), new PublisherClient(sender), options);
    }

    private static Task UnexpectedComplete(CancellationToken _) => throw new AssertFailedException("Unexpected completion");
    private static Task UnexpectedDeadLetter(string _, CancellationToken cancellationToken) => throw new AssertFailedException("Unexpected dead-letter");

    private sealed class RecordingSender : ISender
    {
        public List<IMessage> Messages { get; } = [];
        public bool Fail { get; set; }
        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("unavailable");
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
