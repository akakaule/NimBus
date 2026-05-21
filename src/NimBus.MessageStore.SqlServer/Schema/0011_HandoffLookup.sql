-- Read-side index for IHandoffClient.CompleteAsync / FailAsync. Adapters
-- correlate via ExternalJobId only; the framework looks the pending audit
-- row up here. The filter keeps the index small (pending-handoff is the only
-- slice that's ever queried this way) and side-steps the NULL-storm that
-- legacy completed/failed rows would otherwise contribute.

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'UnresolvedEvents'
      AND i.name = 'IX_UnresolvedEvents_ExternalJobId_PendingHandoff'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_UnresolvedEvents_ExternalJobId_PendingHandoff]
        ON [$schema$].[UnresolvedEvents] ([EndpointId], [ExternalJobId])
        INCLUDE ([EventId], [SessionId], [EventTypeId])
        WHERE [PendingSubStatus] = 'Handoff' AND [Status] = 'Pending' AND [Deleted] = 0;
END
GO
