-- Platform-wide audit-type selection: which operator actions the WebApp records.
-- A single row holding the disabled MessageAuditType names as a JSON array. No seed:
-- a missing row means every type is recorded.
IF OBJECT_ID('[$schema$].[AuditSettings]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AuditSettings] (
        [Id]                  NVARCHAR(50)  NOT NULL PRIMARY KEY,
        [DisabledAuditTypes]  NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_AuditSettings_DisabledAuditTypes] DEFAULT (N'[]'),
        [UpdatedAtUtc]        DATETIME2     NOT NULL CONSTRAINT [DF_AuditSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO
