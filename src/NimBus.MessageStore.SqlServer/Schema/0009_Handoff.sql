-- Async-completion / PendingHandoff sub-status fields. All nullable so existing
-- rows project unchanged: legacy Pending entries report PendingSubStatus=NULL,
-- new entries projected from PendingHandoffResponse report 'Handoff' plus the
-- handler-supplied metadata. Spec: docs/specs/002-async-message-completion.
IF COL_LENGTH('[$schema$].[UnresolvedEvents]', 'PendingSubStatus') IS NULL
BEGIN
    ALTER TABLE [$schema$].[UnresolvedEvents] ADD
        [PendingSubStatus]  NVARCHAR(50)   NULL,
        [HandoffReason]     NVARCHAR(MAX)  NULL,
        [ExternalJobId]     NVARCHAR(500)  NULL,
        [ExpectedBy]        DATETIME2      NULL;
END
GO
