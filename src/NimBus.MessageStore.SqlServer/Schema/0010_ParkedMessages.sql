-- Park-and-replay storage for the transport-agnostic deferred-by-session
-- primitive (PortableDeferredMessageProcessor). See
-- docs/specs/003-rabbitmq-transport/deferred-by-session-design.md.
--
-- (EndpointId, MessageId) is the natural idempotency key — re-parking the
-- same message at the same endpoint is a no-op and returns the existing
-- ParkSequence.
--
-- (EndpointId, SessionKey, ParkSequence) is the natural FIFO key for replay.
-- ParkSequence is allocated monotonically per (EndpointId, SessionKey) by
-- ISessionStateStore.GetNextDeferralSequenceAndIncrement; the unique
-- constraint enforces the invariant.
IF OBJECT_ID('[$schema$].[ParkedMessages]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[ParkedMessages] (
        [Id]                     BIGINT          IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [EndpointId]             NVARCHAR(200)   NOT NULL,
        [SessionKey]             NVARCHAR(200)   NOT NULL,
        [ParkSequence]           BIGINT          NOT NULL,
        [MessageId]              NVARCHAR(200)   NOT NULL,
        [EventId]                NVARCHAR(200)   NOT NULL,
        [EventTypeId]            NVARCHAR(200)   NULL,
        [BlockingEventId]        NVARCHAR(200)   NULL,
        [MessageEnvelopeJson]    NVARCHAR(MAX)   NOT NULL,
        [ParkedAtUtc]            DATETIME2       NOT NULL,
        [ReplayedAtUtc]          DATETIME2       NULL,
        [SkippedAtUtc]           DATETIME2       NULL,
        [DeadLetteredAtUtc]      DATETIME2       NULL,
        [DeadLetterReason]       NVARCHAR(MAX)   NULL,
        [ReplayAttemptCount]     INT             NOT NULL CONSTRAINT [DF_ParkedMessages_ReplayAttemptCount] DEFAULT (0),
        [CreatedAtUtc]           DATETIME2       NOT NULL CONSTRAINT [DF_ParkedMessages_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [UQ_ParkedMessages_Endpoint_MessageId]
            UNIQUE ([EndpointId], [MessageId]),

        CONSTRAINT [UQ_ParkedMessages_Endpoint_Session_Sequence]
            UNIQUE ([EndpointId], [SessionKey], [ParkSequence])
    );

    -- Replay path:
    --   WHERE EndpointId=? AND SessionKey=? AND ParkSequence>?
    --     AND ReplayedAtUtc IS NULL AND SkippedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL
    --   ORDER BY ParkSequence ASC
    -- Filtered to active rows so the index doesn't accumulate terminal-state rows.
    CREATE INDEX [IX_ParkedMessages_Replay]
        ON [$schema$].[ParkedMessages]
        ([EndpointId], [SessionKey], [ParkSequence])
        INCLUDE ([MessageId], [EventId], [EventTypeId], [MessageEnvelopeJson])
        WHERE [ReplayedAtUtc] IS NULL AND [SkippedAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL;

    -- Operator UI: list active parked messages at an endpoint, ordered by park time.
    CREATE INDEX [IX_ParkedMessages_Endpoint_Active]
        ON [$schema$].[ParkedMessages]
        ([EndpointId], [SessionKey], [ParkedAtUtc] DESC)
        WHERE [ReplayedAtUtc] IS NULL AND [SkippedAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL;
END
GO

-- Extend SessionStates with the per-session replay checkpoint and active-park
-- counter (per the design — counter for cheap hot-path reads, with end-of-replay
-- reconciliation against COUNT(*) on ParkedMessages).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'LastReplayedSequence' AND Object_ID = Object_ID(N'[$schema$].[SessionStates]'))
BEGIN
    ALTER TABLE [$schema$].[SessionStates]
        ADD [LastReplayedSequence] INT NOT NULL CONSTRAINT [DF_SessionStates_LastReplayedSequence] DEFAULT (-1);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ActiveParkCount' AND Object_ID = Object_ID(N'[$schema$].[SessionStates]'))
BEGIN
    ALTER TABLE [$schema$].[SessionStates]
        ADD [ActiveParkCount] INT NOT NULL CONSTRAINT [DF_SessionStates_ActiveParkCount] DEFAULT (0);
END
GO
