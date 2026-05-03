-- Per-message persistence shape used by the WebApp's message detail / search /
-- history views. EndpointId is a discriminator (no per-endpoint table) so cross-
-- endpoint queries are trivial.
IF OBJECT_ID('[$schema$].[Messages]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[Messages] (
        [Id]                          BIGINT          IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EventId]                     NVARCHAR(200)   NOT NULL,
        [MessageId]                   NVARCHAR(200)   NOT NULL,
        [EndpointId]                  NVARCHAR(200)   NOT NULL,
        [SessionId]                   NVARCHAR(200)   NULL,
        [CorrelationId]               NVARCHAR(200)   NULL,
        [EventTypeId]                 NVARCHAR(200)   NULL,
        [OriginatingMessageId]        NVARCHAR(200)   NULL,
        [ParentMessageId]             NVARCHAR(200)   NULL,
        [FromAddress]                 NVARCHAR(200)   NULL,
        [ToAddress]                   NVARCHAR(200)   NULL,
        [OriginatingFrom]             NVARCHAR(200)   NULL,
        [OriginalSessionId]           NVARCHAR(200)   NULL,
        [MessageType]                 NVARCHAR(50)    NULL,
        [EndpointRole]                NVARCHAR(50)    NULL,
        [EnqueuedTimeUtc]             DATETIME2       NOT NULL,
        [RetryCount]                  INT             NULL,
        [RetryLimit]                  INT             NULL,
        [DeferralSequence]            INT             NULL,
        [QueueTimeMs]                 BIGINT          NULL,
        [ProcessingTimeMs]            BIGINT          NULL,
        [DeadLetterReason]            NVARCHAR(MAX)   NULL,
        [DeadLetterErrorDescription]  NVARCHAR(MAX)   NULL,
        [MessageContentJson]          NVARCHAR(MAX)   NOT NULL,
        [CreatedAtUtc]                DATETIME2       NOT NULL CONSTRAINT [DF_Messages_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [UQ_Messages_EventId_MessageId] UNIQUE ([EventId], [MessageId])
    );

    CREATE INDEX [IX_Messages_EndpointId_EnqueuedTimeUtc] ON [$schema$].[Messages] ([EndpointId], [EnqueuedTimeUtc] DESC);
    CREATE INDEX [IX_Messages_EventId] ON [$schema$].[Messages] ([EventId]);
    CREATE INDEX [IX_Messages_ToAddress] ON [$schema$].[Messages] ([ToAddress]);
    CREATE INDEX [IX_Messages_EndpointId_EventTypeId_EnqueuedTimeUtc] ON [$schema$].[Messages] ([EndpointId], [EventTypeId], [EnqueuedTimeUtc] DESC);
END
GO
