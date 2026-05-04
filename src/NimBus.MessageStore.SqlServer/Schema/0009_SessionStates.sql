-- Per-session state used by ISessionStateStore: which session is currently
-- blocked by which event, and the deferred-count / deferral-sequence helpers
-- used by the deferred-by-session subscription. EndpointId + SessionId is the
-- natural key. State previously lived in Service Bus session state; relocating
-- it to the message store makes it transport-neutral (RabbitMQ has no
-- equivalent primitive).
IF OBJECT_ID('[$schema$].[SessionStates]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[SessionStates] (
        [EndpointId]            NVARCHAR(200)   NOT NULL,
        [SessionId]             NVARCHAR(200)   NOT NULL,
        [BlockedByEventId]      NVARCHAR(200)   NULL,
        [DeferredCount]         INT             NOT NULL CONSTRAINT [DF_SessionStates_DeferredCount] DEFAULT (0),
        [NextDeferralSequence]  INT             NOT NULL CONSTRAINT [DF_SessionStates_NextDeferralSequence] DEFAULT (0),
        [UpdatedAtUtc]          DATETIME2       NOT NULL CONSTRAINT [DF_SessionStates_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_SessionStates] PRIMARY KEY ([EndpointId], [SessionId])
    );
END
GO
