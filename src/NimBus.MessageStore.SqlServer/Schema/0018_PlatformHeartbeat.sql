-- Platform heartbeat: per-endpoint probe rows, the platform-wide schedule, and
-- liveness of the platform's own services.
--
-- Migration 0006 originally shipped a Heartbeats table and two EndpointMetadata
-- columns; 0010 dropped them because no producer or consumer existed. This
-- migration re-creates them for the revived feature, so every object is guarded:
-- a database that never ran 0010 already has them.

IF OBJECT_ID('[$schema$].[Heartbeats]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[Heartbeats] (
        [Id]                       BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EndpointId]               NVARCHAR(200) NOT NULL,
        [MessageId]                NVARCHAR(200) NULL,
        [StartTimeUtc]             DATETIME2     NOT NULL,
        [ReceivedTimeUtc]          DATETIME2     NOT NULL,
        [EndTimeUtc]               DATETIME2     NOT NULL,
        [EndpointHeartbeatStatus]  NVARCHAR(20)  NOT NULL,
        [SdkVersion]               NVARCHAR(100) NULL
    );

    CREATE INDEX [IX_Heartbeats_EndpointId_ReceivedTimeUtc] ON [$schema$].[Heartbeats] ([EndpointId], [ReceivedTimeUtc] DESC);
END
GO

-- A pre-0010 database has the table but not the column 0015 added in the fork parent.
IF COL_LENGTH('[$schema$].[Heartbeats]', 'SdkVersion') IS NULL
BEGIN
    ALTER TABLE [$schema$].[Heartbeats] ADD [SdkVersion] NVARCHAR(100) NULL;
END
GO

IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'IsHeartbeatEnabled') IS NULL
BEGIN
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [IsHeartbeatEnabled] BIT NULL;
END
GO

IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'EndpointHeartbeatStatus') IS NULL
BEGIN
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [EndpointHeartbeatStatus] NVARCHAR(20) NULL;
END
GO

-- Single-row schedule. The seed keeps the feature off until an operator enables it.
IF OBJECT_ID('[$schema$].[HeartbeatSettings]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[HeartbeatSettings] (
        [Id]                 NVARCHAR(50) NOT NULL PRIMARY KEY,
        [Enabled]            BIT          NOT NULL CONSTRAINT [DF_HeartbeatSettings_Enabled] DEFAULT (0),
        [IntervalSeconds]    INT          NOT NULL CONSTRAINT [DF_HeartbeatSettings_IntervalSeconds] DEFAULT (300),
        [TimeoutSeconds]     INT          NOT NULL CONSTRAINT [DF_HeartbeatSettings_TimeoutSeconds] DEFAULT (60),
        [LastSentAtUtc]      DATETIME2    NULL,
        [UpdatedAtUtc]       DATETIME2    NOT NULL CONSTRAINT [DF_HeartbeatSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM [$schema$].[HeartbeatSettings] WHERE [Id] = N'HeartbeatSettings')
BEGIN
    INSERT INTO [$schema$].[HeartbeatSettings] ([Id], [Enabled], [IntervalSeconds], [TimeoutSeconds])
    VALUES (N'HeartbeatSettings', 0, 300, 60);
END
GO

-- Liveness of the platform's own services (currently the Resolver), measured by a
-- round-trip probe. Kept apart from [Heartbeats], which is keyed by endpoint.
--
-- A probe is in flight exactly while [LastProbeMessageId] is not null;
-- [Status] holds the last settled outcome so an in-flight probe never masks a
-- service that is known to be down.
IF OBJECT_ID('[$schema$].[ServiceHealth]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[ServiceHealth] (
        [ServiceId]          NVARCHAR(100) NOT NULL PRIMARY KEY,
        [Status]             NVARCHAR(20)  NOT NULL CONSTRAINT [DF_ServiceHealth_Status] DEFAULT (N'Unknown'),
        [Version]            NVARCHAR(100) NULL,
        [LastProbeMessageId] NVARCHAR(100) NULL,
        [LastProbeSentUtc]   DATETIME2     NULL,
        [LastSeenUtc]        DATETIME2     NULL,
        [RoundTripMs]        BIGINT        NULL,
        [UpdatedAtUtc]       DATETIME2     NOT NULL CONSTRAINT [DF_ServiceHealth_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM [$schema$].[ServiceHealth] WHERE [ServiceId] = N'Resolver')
BEGIN
    INSERT INTO [$schema$].[ServiceHealth] ([ServiceId], [Status])
    VALUES (N'Resolver', N'Unknown');
END
GO
