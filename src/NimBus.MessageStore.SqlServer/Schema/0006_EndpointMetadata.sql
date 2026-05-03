IF OBJECT_ID('[$schema$].[EndpointMetadata]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EndpointMetadata] (
        [EndpointId]                 NVARCHAR(200) NOT NULL PRIMARY KEY,
        [EndpointOwner]              NVARCHAR(200) NULL,
        [EndpointOwnerTeam]          NVARCHAR(200) NULL,
        [EndpointOwnerEmail]         NVARCHAR(400) NULL,
        [IsHeartbeatEnabled]         BIT           NULL,
        [EndpointHeartbeatStatus]    NVARCHAR(20)  NULL,
        [TechnicalContactsJson]      NVARCHAR(MAX) NULL,
        [SubscriptionStatus]         BIT           NULL,
        [UpdatedAtUtc]               DATETIME2     NOT NULL CONSTRAINT [DF_Metadata_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF OBJECT_ID('[$schema$].[Heartbeats]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[Heartbeats] (
        [Id]                       BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EndpointId]               NVARCHAR(200) NOT NULL,
        [MessageId]                NVARCHAR(200) NULL,
        [StartTimeUtc]             DATETIME2     NOT NULL,
        [ReceivedTimeUtc]          DATETIME2     NOT NULL,
        [EndTimeUtc]               DATETIME2     NOT NULL,
        [EndpointHeartbeatStatus]  NVARCHAR(20)  NOT NULL
    );

    CREATE INDEX [IX_Heartbeats_EndpointId_ReceivedTimeUtc] ON [$schema$].[Heartbeats] ([EndpointId], [ReceivedTimeUtc] DESC);
END
GO
