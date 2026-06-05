IF OBJECT_ID('[$schema$].[EventSchemas]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EventSchemas] (
        [EventTypeId]    NVARCHAR(200) NOT NULL PRIMARY KEY,
        [Name]           NVARCHAR(400) NULL,
        [JsonSchema]     NVARCHAR(MAX) NOT NULL,
        [Description]    NVARCHAR(MAX) NULL,
        [SessionKeyPath] NVARCHAR(400) NULL,
        [Version]        INT NOT NULL DEFAULT(1),
        [AgentId]        NVARCHAR(200) NULL,
        [CreatedBy]      NVARCHAR(200) NULL,
        [CreatedUtc]     DATETIME2 NOT NULL
    );
END
GO
