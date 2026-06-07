IF OBJECT_ID('[$schema$].[EventMappings]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EventMappings] (
        [Id]                 NVARCHAR(400)  NOT NULL PRIMARY KEY,
        [SourceEventTypeId]  NVARCHAR(200)  NOT NULL,
        [TargetEventTypeId]  NVARCHAR(200)  NOT NULL,
        [Transform]          NVARCHAR(MAX)  NOT NULL,
        [Rationale]          NVARCHAR(MAX)  NULL,
        [WorkedExamplesJson] NVARCHAR(MAX)  NULL,
        [SourceSchemaHash]   NVARCHAR(200)  NOT NULL,
        [State]              NVARCHAR(50)   NOT NULL,
        [Version]            INT            NOT NULL DEFAULT(1),
        [CreatedBy]          NVARCHAR(200)  NULL,
        [CreatedUtc]         DATETIME2      NOT NULL,
        [ApprovedBy]         NVARCHAR(200)  NULL,
        [ApprovedUtc]        DATETIME2      NULL
    );

    CREATE INDEX [IX_EventMappings_SourceState]
        ON [$schema$].[EventMappings] ([SourceEventTypeId], [State]);
END
GO
