namespace NimBus.ServiceBusEmulator.Broker;

internal enum BrokerEntityStatus
{
    Active,
    ReceiveDisabled,
    SendDisabled,
}

internal sealed class BrokerOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public TimeSpan DefaultLockDuration { get; init; } = TimeSpan.FromSeconds(30);

    public long MaxStoredBytes { get; init; } = 512L * 1024 * 1024;
}

internal sealed class BrokerQuotaExceededException(string message) : InvalidOperationException(message);

internal sealed class SessionCannotBeLockedException(string message) : InvalidOperationException(message);

internal sealed record TopicDefinition(string Name)
{
    public BrokerEntityStatus Status { get; init; } = BrokerEntityStatus.Active;

    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    public long MaxSizeInMegabytes { get; init; } = 1024;

    public bool RequiresDuplicateDetection { get; init; }

    public TimeSpan DuplicateDetectionHistoryTimeWindow { get; init; } = TimeSpan.FromMinutes(10);

    public bool EnableBatchedOperations { get; init; } = true;

    public bool SupportOrdering { get; init; }
}

internal sealed record SubscriptionDefinition(string Name)
{
    public bool RequiresSession { get; init; }

    public int MaxDeliveryCount { get; init; } = BrokerDefaults.MaxDeliveryCount;

    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    public bool DeadLetterOnFilterEvaluationExceptions { get; init; } = true;

    public string? ForwardTo { get; init; }

    public BrokerEntityStatus Status { get; init; } = BrokerEntityStatus.Active;
}

internal sealed record RuleDefinition(string Name, string FilterExpression, string? ActionExpression = null);

internal sealed record BrokerMessage
{
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;

    public string? MessageId { get; init; }

    public string? SessionId { get; init; }

    public string? CorrelationId { get; init; }

    public string? ReplyTo { get; init; }

    public string? ReplyToSessionId { get; init; }

    public string? ContentType { get; init; }

    public string? Subject { get; init; }

    public string? To { get; init; }

    public string? PartitionKey { get; init; }

    public string? TransactionPartitionKey { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public DateTimeOffset? ScheduledEnqueueTime { get; init; }

    public IDictionary<string, object?> ApplicationProperties { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public long SequenceNumber { get; internal set; }

    public DateTimeOffset EnqueuedTime { get; internal set; }

    public int DeliveryCount { get; internal set; }

    public DateTimeOffset LockedUntil { get; internal set; }

    public Guid LockToken { get; internal set; }

    internal int ForwardHopCount { get; set; }

    internal BrokerMessage Copy()
    {
        return this with
        {
            Body = Body.ToArray(),
            ApplicationProperties = new Dictionary<string, object?>(ApplicationProperties, StringComparer.Ordinal),
            LockToken = Guid.Empty,
            LockedUntil = default,
            DeliveryCount = 0,
        };
    }
}

internal sealed record BrokerDelivery(BrokerMessage Message, Guid LockToken);

internal sealed record AcceptedSession(string SessionId, DateTimeOffset LockedUntil);

internal sealed record SubscriptionRuntimeProperties(
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long TransferMessageCount,
    long TransferDeadLetterMessageCount)
{
    public long TotalMessageCount =>
        ActiveMessageCount + DeadLetterMessageCount + TransferMessageCount + TransferDeadLetterMessageCount;
}

internal sealed record TopicRuntimeProperties(
    string Name,
    long SubscriptionCount,
    long ScheduledMessageCount,
    long SizeInBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset AccessedAt);

internal sealed record AdminOperation(string Verb, string EntityPath, string Kind, DateTimeOffset Timestamp);

internal sealed record TopologySnapshot(IReadOnlyList<TopologyTopic> Topics);

internal sealed record TopologyTopic(
    TopicDefinition Definition,
    IReadOnlyList<TopologySubscription> Subscriptions);

internal sealed record TopologySubscription(
    SubscriptionDefinition Definition,
    IReadOnlyList<RuleDefinition> Rules);

internal sealed record PreparedTopologyMutation(TopologySnapshot Snapshot, Action Apply);
