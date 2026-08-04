-- Spec 025: preserve scheduled-message (workflow timeout) identity through the
-- audit chain. ScheduledMessageId is the logical TimeoutId (stable across
-- retries and resubmission), ScheduledEnqueueTimeUtc the original due time,
-- and WorkflowCorrelationId the workflow conversation ID carried on
-- Resolver-bound responses (the row's own CorrelationId keeps the = MessageId
-- audit-linkage convention). All columns are nullable so existing rows and
-- ordinary messages continue to project as null.
IF COL_LENGTH('[$schema$].[Messages]', 'ScheduledMessageId') IS NULL
BEGIN
    ALTER TABLE [$schema$].[Messages] ADD
        [ScheduledMessageId]      NVARCHAR(128) NULL,
        [ScheduledEnqueueTimeUtc] DATETIMEOFFSET NULL,
        [WorkflowCorrelationId]   NVARCHAR(256) NULL;
END
GO

IF COL_LENGTH('[$schema$].[UnresolvedEvents]', 'ScheduledMessageId') IS NULL
BEGIN
    ALTER TABLE [$schema$].[UnresolvedEvents] ADD
        [ScheduledMessageId]      NVARCHAR(128) NULL,
        [ScheduledEnqueueTimeUtc] DATETIMEOFFSET NULL,
        [WorkflowCorrelationId]   NVARCHAR(256) NULL;
END
GO
