-- Resolver state per (EndpointId, EventId, SessionId). One row per "live" event.
-- ROWVERSION column gives optimistic concurrency for resolver writes; combined
-- with the natural-key uniqueness constraint, MERGE upserts are idempotent.
IF OBJECT_ID('[$schema$].[UnresolvedEvents]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[UnresolvedEvents] (
        [Id]                          BIGINT          IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EventId]                     NVARCHAR(200)   NOT NULL,
        [SessionId]                   NVARCHAR(200)   NULL,
        [EndpointId]                  NVARCHAR(200)   NOT NULL,
        [Status]                      NVARCHAR(50)    NOT NULL,
        [UpdatedAtUtc]                DATETIME2       NOT NULL,
        [EnqueuedTimeUtc]             DATETIME2       NOT NULL,
        [CorrelationId]               NVARCHAR(200)   NULL,
        [EndpointRole]                NVARCHAR(50)    NULL,
        [MessageType]                 NVARCHAR(50)    NULL,
        [RetryCount]                  INT             NULL,
        [RetryLimit]                  INT             NULL,
        [LastMessageId]               NVARCHAR(200)   NULL,
        [OriginatingMessageId]        NVARCHAR(200)   NULL,
        [ParentMessageId]             NVARCHAR(200)   NULL,
        [OriginatingFrom]             NVARCHAR(200)   NULL,
        [Reason]                      NVARCHAR(MAX)   NULL,
        [DeadLetterReason]            NVARCHAR(MAX)   NULL,
        [DeadLetterErrorDescription]  NVARCHAR(MAX)   NULL,
        [EventTypeId]                 NVARCHAR(200)   NULL,
        [ToAddress]                   NVARCHAR(200)   NULL,
        [FromAddress]                 NVARCHAR(200)   NULL,
        [QueueTimeMs]                 BIGINT          NULL,
        [ProcessingTimeMs]            BIGINT          NULL,
        [MessageContentJson]          NVARCHAR(MAX)   NULL,
        [Deleted]                     BIT             NOT NULL CONSTRAINT [DF_UnresolvedEvents_Deleted] DEFAULT (0),
        [RowVersion]                  ROWVERSION      NOT NULL,
        CONSTRAINT [UQ_UnresolvedEvents_Endpoint_Event_Session] UNIQUE ([EndpointId], [EventId], [SessionId])
    );

    CREATE INDEX [IX_UnresolvedEvents_EndpointId_Status] ON [$schema$].[UnresolvedEvents] ([EndpointId], [Status]) WHERE [Deleted] = 0;
    CREATE INDEX [IX_UnresolvedEvents_EndpointId_SessionId_Status] ON [$schema$].[UnresolvedEvents] ([EndpointId], [SessionId], [Status]) WHERE [Deleted] = 0;
    CREATE INDEX [IX_UnresolvedEvents_EndpointId_UpdatedAtUtc] ON [$schema$].[UnresolvedEvents] ([EndpointId], [UpdatedAtUtc] DESC) WHERE [Deleted] = 0;
END
GO
