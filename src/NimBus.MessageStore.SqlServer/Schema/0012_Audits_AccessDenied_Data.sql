-- Spec 008: centralized audit log service. Extends the existing
-- [$schema$].[MessageAudits] table (provisioned in 0004_Audits.sql) with two
-- new columns required by IAuditLogService:
--
--   AccessDenied BIT NOT NULL DEFAULT 0  — flags rows written by the
--     access-denied branch of a privileged action (the user attempted the
--     action but did not have permission). Defaults to 0 so legacy rows
--     project unchanged.
--   Data NVARCHAR(MAX) NULL              — structured action context (e.g.
--     serialized search filter, ResubmitWithChanges body). Truncated at the
--     writer to ~4 KB; NVARCHAR(MAX) is the safe storage type.
--
-- EventId and EndpointId columns already exist on the table per
-- 0004_Audits.sql — DO NOT add duplicates.

IF COL_LENGTH('[$schema$].[MessageAudits]', 'AccessDenied') IS NULL
BEGIN
    ALTER TABLE [$schema$].[MessageAudits]
        ADD [AccessDenied] BIT NOT NULL
            CONSTRAINT [DF_MessageAudits_AccessDenied] DEFAULT (0);
END
GO

IF COL_LENGTH('[$schema$].[MessageAudits]', 'Data') IS NULL
BEGIN
    ALTER TABLE [$schema$].[MessageAudits]
        ADD [Data] NVARCHAR(MAX) NULL;
END
GO
