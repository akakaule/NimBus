IF OBJECT_ID('[$schema$].[MessageAudits]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[MessageAudits] (
        [Id]              BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EventId]         NVARCHAR(200) NOT NULL,
        [EndpointId]      NVARCHAR(200) NULL,
        [EventTypeId]     NVARCHAR(200) NULL,
        [AuditorName]     NVARCHAR(200) NULL,
        [AuditTimestamp]  DATETIME2     NOT NULL,
        [AuditType]       NVARCHAR(50)  NOT NULL,
        [Comment]         NVARCHAR(MAX) NULL,
        [CreatedAtUtc]    DATETIME2     NOT NULL CONSTRAINT [DF_MessageAudits_CreatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );

    CREATE INDEX [IX_MessageAudits_EventId] ON [$schema$].[MessageAudits] ([EventId]);
    CREATE INDEX [IX_MessageAudits_Endpoint_EventType_Created] ON [$schema$].[MessageAudits] ([EndpointId], [EventTypeId], [CreatedAtUtc] DESC);
END
GO
