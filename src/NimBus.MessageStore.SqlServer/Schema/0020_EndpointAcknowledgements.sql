-- Monitor acknowledgements: at most one per endpoint, shared by every Monitor client.
-- Expiry and clear-on-recovery are WebApp policy; rows are removed by the WebApp.
IF OBJECT_ID('[$schema$].[EndpointAcknowledgements]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EndpointAcknowledgements] (
        [EndpointId]                   NVARCHAR(200) NOT NULL,
        [AcknowledgementId]            NVARCHAR(64)  NOT NULL,
        [Reason]                       NVARCHAR(500) NOT NULL,
        [AcknowledgedBy]               NVARCHAR(256) NULL,
        [AcknowledgedAtUtc]            DATETIME2     NOT NULL,
        [ExpiresAtUtc]                 DATETIME2     NOT NULL,
        [FailedCountAtAcknowledgement] INT           NOT NULL,
        CONSTRAINT [PK_EndpointAcknowledgements] PRIMARY KEY ([EndpointId])
    );
END
GO
