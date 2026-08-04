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

        // CloudEvents identity carried from a CloudEvents-consuming subscriber to the
        // Resolver on the response message, so the tracking/audit record preserves the
        // inbound CloudEvent's identity. Absent (null) on native messages, so a native
        // response is byte-identical on the wire.
        CloudEventId,
        CloudEventSource,
        CloudEventType,
        CloudEventSubject,

        // Scheduled-message (workflow timeout) identity, spec 025. ScheduledMessageId
        // is the logical TimeoutId, stable across retries/redeliveries while each
        // attempt mints its own transport MessageId. Absent (null) on ordinary
        // messages, so an unmarked message is byte-identical on the wire.
        ScheduledMessageId,
        ScheduledEnqueueTimeUtc,
        WorkflowCorrelationId,
    }
}
