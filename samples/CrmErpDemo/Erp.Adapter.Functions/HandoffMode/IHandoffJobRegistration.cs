namespace Erp.Adapter.Functions.HandoffMode;

public interface IHandoffJobRegistration
{
    Task RegisterAsync(HandoffJob job, CancellationToken cancellationToken);
}

// Mirrors Erp.Api.HandoffMode.HandoffJob (the receiving DTO). The first six fields
// match the MessageEntity shape that ManagerClient.CompleteHandoff / FailHandoff read,
// so Erp.Api can settle the message without dragging in the message store.
public sealed record HandoffJob
{
    public required string EventId { get; init; }
    public required string SessionId { get; init; }
    public required string MessageId { get; init; }
    public string? OriginatingMessageId { get; init; }
    public required string EventTypeId { get; init; }
    public string? CorrelationId { get; init; }
    public required string ExternalJobId { get; init; }
    public required DateTime DueAt { get; init; }
    public required string PayloadJson { get; init; }
}
