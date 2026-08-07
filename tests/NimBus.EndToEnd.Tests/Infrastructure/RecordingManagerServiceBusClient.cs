using Azure.Messaging.ServiceBus;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// The minimum ServiceBusClient surface <c>ManagerClient</c> touches: create a
/// sender for the destination endpoint and record what it sends. Lets an
/// end-to-end test drive the REAL manager resubmission and then feed its actual
/// wire message back into the subscriber pipeline.
/// </summary>
internal sealed class RecordingManagerServiceBusClient : ServiceBusClient
{
    public RecordingManagerSender Sender { get; } = new();

    /// <summary>The entity path the manager addressed its resubmission to.</summary>
    public string? LastSenderEntityPath { get; private set; }

    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        LastSenderEntityPath = queueOrTopicName;
        return Sender;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options)
    {
        LastSenderEntityPath = queueOrTopicName;
        return Sender;
    }
}

internal sealed class RecordingManagerSender : ServiceBusSender
{
    public List<ServiceBusMessage> SentMessages { get; } = new();

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    // ManagerClient owns its sender with `await using`; the base implementations
    // reach for a live AMQP link that this double never had.
    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
