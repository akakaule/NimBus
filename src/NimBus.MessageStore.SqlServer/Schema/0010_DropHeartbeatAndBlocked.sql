-- Drop the legacy heartbeat plumbing and the forward-declared blocked /
-- invalid projections. Neither feature shipped a producer or consumer in
-- production code, so the tables only ever stored zero rows. See git history
-- for the original migrations 0006 (Heartbeats), 0007 (BlockedMessages /
-- InvalidMessages).

IF OBJECT_ID('[$schema$].[Heartbeats]', 'U') IS NOT NULL
BEGIN
    DROP TABLE [$schema$].[Heartbeats];
END
GO

IF OBJECT_ID('[$schema$].[BlockedMessages]', 'U') IS NOT NULL
BEGIN
    DROP TABLE [$schema$].[BlockedMessages];
END
GO

IF OBJECT_ID('[$schema$].[InvalidMessages]', 'U') IS NOT NULL
BEGIN
    DROP TABLE [$schema$].[InvalidMessages];
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$' AND t.name = 'EndpointMetadata' AND c.name = 'IsHeartbeatEnabled'
)
BEGIN
    ALTER TABLE [$schema$].[EndpointMetadata] DROP COLUMN [IsHeartbeatEnabled];
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$' AND t.name = 'EndpointMetadata' AND c.name = 'EndpointHeartbeatStatus'
)
BEGIN
    ALTER TABLE [$schema$].[EndpointMetadata] DROP COLUMN [EndpointHeartbeatStatus];
END
GO
