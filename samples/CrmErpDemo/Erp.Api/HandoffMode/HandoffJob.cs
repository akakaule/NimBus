namespace Erp.Api.HandoffMode;

// In-flight handoff job tracked by Erp.Api on behalf of the ERP adapter. The
// adapter POSTs one of these when its CrmAccountCreated handler signals
// PendingHandoff. The BackgroundService picks it up after DueAt and drives
// settlement via IManagerClient.CompleteHandoff / FailHandoff.
//
// The first six fields are exactly what ManagerClient.CompleteHandoff /
// FailHandoff read off MessageEntity, so we can construct a minimal stub
// without dragging the message store into Erp.Api.
//
// PayloadJson carries the original event so the BackgroundService can apply
// the ERP-side write before signalling Completed (the user handler is NOT
// re-invoked on settlement, per the PendingHandoff spec).
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
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
}
