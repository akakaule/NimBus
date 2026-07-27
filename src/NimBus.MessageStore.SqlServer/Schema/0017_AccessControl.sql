-- Access-control lists (spec 026). One row per ACL document: the single
-- site-wide ACL (Id = 'site') and one per endpoint (Id = 'endpoint:{endpointId}').
-- The whole AccessControlList is stored as opaque JSON — role lists are read and
-- replaced as a unit, mirroring the Cosmos single-document model.
IF OBJECT_ID('[$schema$].[AccessControl]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AccessControl] (
        [Id]           NVARCHAR(220) NOT NULL,  -- 'site' | 'endpoint:{id}' (endpointId <= 200)
        [ContentJson]  NVARCHAR(MAX) NOT NULL,
        [UpdatedAtUtc] DATETIME2     NOT NULL CONSTRAINT [DF_AccessControl_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_AccessControl] PRIMARY KEY ([Id])
    );
END
GO
