-- Date-leading sort indexes for the WebApp's default (unfiltered) searches and the
-- date-range metrics queries. Every existing index on these tables leads with
-- EndpointId, so an unfiltered landing search — which sorts by date DESC — full-scans
-- and sorts the whole table, and the metrics range scans (WHERE EnqueuedTimeUtc >= @From)
-- have no supporting index. These key-order-matching indexes let the sort/range be
-- served directly.
--   SearchMessages    ORDER BY EnqueuedTimeUtc DESC, Id DESC (+ metrics date-range scans)
--   GetEventsByFilter ORDER BY UpdatedAtUtc  DESC, Id DESC
--   SearchAudits      ORDER BY CreatedAtUtc  DESC, Id DESC

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'Messages'
      AND i.name = 'IX_Messages_EnqueuedTimeUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Messages_EnqueuedTimeUtc]
        ON [$schema$].[Messages] ([EnqueuedTimeUtc] DESC, [Id] DESC);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'UnresolvedEvents'
      AND i.name = 'IX_UnresolvedEvents_UpdatedAtUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_UnresolvedEvents_UpdatedAtUtc]
        ON [$schema$].[UnresolvedEvents] ([UpdatedAtUtc] DESC, [Id] DESC)
        WHERE [Deleted] = 0;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'MessageAudits'
      AND i.name = 'IX_MessageAudits_CreatedAtUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MessageAudits_CreatedAtUtc]
        ON [$schema$].[MessageAudits] ([CreatedAtUtc] DESC, [Id] DESC);
END
GO
