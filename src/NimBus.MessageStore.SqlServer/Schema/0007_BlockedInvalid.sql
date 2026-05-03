-- Lightweight projections for the WebApp's blocked/invalid event listings.
-- Populated alongside UnresolvedEvents writes.

IF OBJECT_ID('[$schema$].[BlockedMessages]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[BlockedMessages] (
        [Id]            BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EndpointId]    NVARCHAR(200) NOT NULL,
        [SessionId]     NVARCHAR(200) NULL,
        [EventId]       NVARCHAR(200) NOT NULL,
        [OriginatingId] NVARCHAR(200) NULL,
        [Status]        NVARCHAR(50)  NOT NULL,
        [CreatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_BlockedMessages_CreatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );

    CREATE INDEX [IX_BlockedMessages_EndpointId_SessionId] ON [$schema$].[BlockedMessages] ([EndpointId], [SessionId]);
END
GO

IF OBJECT_ID('[$schema$].[InvalidMessages]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[InvalidMessages] (
        [Id]            BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EndpointId]    NVARCHAR(200) NOT NULL,
        [SessionId]     NVARCHAR(200) NULL,
        [EventId]       NVARCHAR(200) NOT NULL,
        [Reason]        NVARCHAR(MAX) NULL,
        [CreatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_InvalidMessages_CreatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );

    CREATE INDEX [IX_InvalidMessages_EndpointId] ON [$schema$].[InvalidMessages] ([EndpointId]);
END
GO
