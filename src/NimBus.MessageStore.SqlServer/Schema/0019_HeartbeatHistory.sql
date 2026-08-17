-- Durable endpoint heartbeat history and the independent fold claim.

IF COL_LENGTH('[$schema$].[Heartbeats]', 'IntervalSeconds') IS NULL
BEGIN
    ALTER TABLE [$schema$].[Heartbeats]
        ADD [IntervalSeconds] INT NOT NULL CONSTRAINT [DF_Heartbeats_IntervalSeconds] DEFAULT (0);
END
GO

IF COL_LENGTH('[$schema$].[HeartbeatSettings]', 'LastHeartbeatFoldAtUtc') IS NULL
BEGIN
    ALTER TABLE [$schema$].[HeartbeatSettings] ADD [LastHeartbeatFoldAtUtc] DATETIME2 NULL;
END
GO

IF OBJECT_ID('[$schema$].[HeartbeatUptimeDays]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[HeartbeatUptimeDays] (
        [EndpointId]        NVARCHAR(200) NOT NULL,
        [DayUtc]            DATETIME2     NOT NULL,
        [Expected]          INT           NOT NULL,
        [Received]          INT           NOT NULL,
        [Missed]            INT           NOT NULL,
        [ObservedSeconds]   INT           NOT NULL,
        [LongestGapSeconds] INT           NOT NULL,
        [LastBeatUtc]       DATETIME2     NOT NULL,
        CONSTRAINT [PK_HeartbeatUptimeDays] PRIMARY KEY ([EndpointId], [DayUtc])
    );

    CREATE INDEX [IX_HeartbeatUptimeDays_DayUtc]
        ON [$schema$].[HeartbeatUptimeDays] ([DayUtc]) INCLUDE ([EndpointId]);
END
GO

IF OBJECT_ID('[$schema$].[HeartbeatGaps]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[HeartbeatGaps] (
        [EndpointId]       NVARCHAR(200) NOT NULL,
        [FromUtc]          DATETIME2     NOT NULL,
        [ToUtc]            DATETIME2     NULL,
        [SdkVersionBefore] NVARCHAR(100) NULL,
        [SdkVersionAfter]  NVARCHAR(100) NULL,
        CONSTRAINT [PK_HeartbeatGaps] PRIMARY KEY ([EndpointId], [FromUtc])
    );

    CREATE INDEX [IX_HeartbeatGaps_ToUtc] ON [$schema$].[HeartbeatGaps] ([ToUtc]);
END
GO
