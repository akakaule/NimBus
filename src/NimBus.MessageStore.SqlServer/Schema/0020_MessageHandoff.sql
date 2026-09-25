-- PendingHandoff metadata on per-message history rows. 0009_Handoff.sql added
-- these fields only to [UnresolvedEvents], so MessageEntity's handoff fields were
-- dropped on SQL Server message rows while Cosmos DB and in-memory kept them.
-- Types mirror 0009. All nullable so existing rows project as null.
IF COL_LENGTH('[$schema$].[Messages]', 'PendingSubStatus') IS NULL
BEGIN
    ALTER TABLE [$schema$].[Messages] ADD
        [PendingSubStatus]  NVARCHAR(50)   NULL,
        [HandoffReason]     NVARCHAR(MAX)  NULL,
        [ExternalJobId]     NVARCHAR(500)  NULL,
        [ExpectedBy]        DATETIME2      NULL;
END
GO
