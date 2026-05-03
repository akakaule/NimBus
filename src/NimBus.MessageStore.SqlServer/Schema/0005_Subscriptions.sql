IF OBJECT_ID('[$schema$].[EndpointSubscriptions]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EndpointSubscriptions] (
        [Id]                    NVARCHAR(200) NOT NULL PRIMARY KEY,
        [EndpointId]            NVARCHAR(200) NOT NULL,
        [Type]                  NVARCHAR(100) NULL,
        [NotificationSeverity]  NVARCHAR(50)  NULL,
        [Mail]                  NVARCHAR(400) NULL,
        [AuthorId]              NVARCHAR(200) NULL,
        [NotifiedAt]            NVARCHAR(50)  NULL,
        [ErrorList]             NVARCHAR(MAX) NULL,
        [Url]                   NVARCHAR(2000) NULL,
        [EventTypesJson]        NVARCHAR(MAX) NULL,
        [Payload]               NVARCHAR(MAX) NULL,
        [Frequency]             INT           NOT NULL CONSTRAINT [DF_Subs_Frequency] DEFAULT (0)
    );

    CREATE INDEX [IX_Subscriptions_EndpointId] ON [$schema$].[EndpointSubscriptions] ([EndpointId]);
    CREATE INDEX [IX_Subscriptions_EndpointId_Mail] ON [$schema$].[EndpointSubscriptions] ([EndpointId], [Mail]);
END
GO
