-- Per-event "reported" marker. A manual operational flag (not a ticket-system
-- integration) so multiple operators can see that a failed event has already
-- been reported. One row per (EndpointId, EventId); upserted on toggle.
-- TicketId captures the external ticket reference the event was reported under
-- (null when marked reported without a ticket).
IF OBJECT_ID('[$schema$].[EventReports]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EventReports] (
        [EndpointId]    NVARCHAR(200) NOT NULL,
        [EventId]       NVARCHAR(200) NOT NULL,
        [IsReported]    BIT           NOT NULL CONSTRAINT [DF_EventReports_IsReported] DEFAULT (0),
        [ReportedBy]    NVARCHAR(200) NULL,
        [ReportedAtUtc] DATETIME2     NULL,
        [TicketId]      NVARCHAR(64)  NULL,
        CONSTRAINT [PK_EventReports] PRIMARY KEY ([EndpointId], [EventId])
    );
END
GO
