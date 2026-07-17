-- Preserve CloudEvents context on per-message history, current tracked-event,
-- and audit rows. All columns are nullable so existing native-message rows
-- continue to project as null.
IF COL_LENGTH('[$schema$].[Messages]', 'CloudEventId') IS NULL
BEGIN
    ALTER TABLE [$schema$].[Messages] ADD
        [CloudEventId]       NVARCHAR(MAX) NULL,
        [CloudEventSource]   NVARCHAR(MAX) NULL,
        [CloudEventType]     NVARCHAR(MAX) NULL,
        [CloudEventSubject]  NVARCHAR(MAX) NULL;
END
GO

IF COL_LENGTH('[$schema$].[MessageAudits]', 'CloudEventId') IS NULL
BEGIN
    ALTER TABLE [$schema$].[MessageAudits] ADD
        [CloudEventId]       NVARCHAR(MAX) NULL,
        [CloudEventSource]   NVARCHAR(MAX) NULL,
        [CloudEventType]     NVARCHAR(MAX) NULL,
        [CloudEventSubject]  NVARCHAR(MAX) NULL;
END
GO

IF COL_LENGTH('[$schema$].[UnresolvedEvents]', 'CloudEventId') IS NULL
BEGIN
    ALTER TABLE [$schema$].[UnresolvedEvents] ADD
        [CloudEventId]       NVARCHAR(MAX) NULL,
        [CloudEventSource]   NVARCHAR(MAX) NULL,
        [CloudEventType]     NVARCHAR(MAX) NULL,
        [CloudEventSubject]  NVARCHAR(MAX) NULL;
END
GO
