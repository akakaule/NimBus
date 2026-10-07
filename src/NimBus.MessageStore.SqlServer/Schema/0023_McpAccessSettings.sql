-- Spec 037: the site Owner's MCP access policy. A single row holding the policy as JSON,
-- fenced by Revision so a stale editor cannot overwrite a newer save. No seed: a missing
-- row means the defaults, which reproduce the behaviour from before the policy existed.
IF OBJECT_ID('[$schema$].[McpAccessSettings]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[McpAccessSettings] (
        [Id]            NVARCHAR(50)  NOT NULL PRIMARY KEY,
        [Revision]      NVARCHAR(36)  NOT NULL,
        [SettingsJson]  NVARCHAR(MAX) NOT NULL,
        [UpdatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_McpAccessSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO
