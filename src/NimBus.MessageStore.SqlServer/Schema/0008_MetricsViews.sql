-- Indexed views for fast metrics queries. Cosmos derives these via SQL GROUP BY
-- per call; SQL Server materializes them so the WebApp queries stay sub-second.

IF NOT EXISTS (SELECT 1 FROM sys.views WHERE name = 'EndpointEventTypeCounts' AND schema_id = SCHEMA_ID('$schema$'))
BEGIN
    EXEC('
        CREATE VIEW [$schema$].[EndpointEventTypeCounts] WITH SCHEMABINDING AS
        SELECT
            m.EndpointId,
            m.EventTypeId,
            m.MessageType,
            COUNT_BIG(*) AS EventCount
        FROM [$schema$].[Messages] m
        GROUP BY m.EndpointId, m.EventTypeId, m.MessageType;
    ');
END
GO

-- Failed-message insights view (recent only; consumers further filter by date)
IF NOT EXISTS (SELECT 1 FROM sys.views WHERE name = 'FailedMessageInsights' AND schema_id = SCHEMA_ID('$schema$'))
BEGIN
    EXEC('
        CREATE VIEW [$schema$].[FailedMessageInsights] AS
        SELECT
            ue.EndpointId,
            ue.EventTypeId,
            ue.DeadLetterErrorDescription AS ErrorText,
            ue.EnqueuedTimeUtc,
            ue.EventId
        FROM [$schema$].[UnresolvedEvents] ue
        WHERE ue.Status IN (N''Failed'', N''DeadLettered'')
          AND ue.Deleted = 0;
    ');
END
GO
