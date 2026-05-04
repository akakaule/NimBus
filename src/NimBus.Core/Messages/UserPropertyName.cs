namespace NimBus.Core.Messages
{
    public enum UserPropertyName
    {
        From,
        To,
        MessageType,
        EventId,
        OriginatingMessageId,
        ParentMessageId,
        RetryLimit,
        RetryCount,
        OriginatingFrom,
        DeadLetterErrorDescription,
        DeadLetterReason,
        EventTypeId,
        OriginalSessionId,
        DeferralSequence,
        QueueTimeMs,
        ProcessingTimeMs,
        HandoffReason,
        ExternalJobId,
        ExpectedBy,
    }
}
