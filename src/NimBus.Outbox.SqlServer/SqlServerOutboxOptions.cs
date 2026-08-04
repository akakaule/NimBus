using System;
using NimBus.Core.Outbox;

namespace NimBus.Outbox.SqlServer
{
    /// <summary>
    /// Delivery mode for scheduled outbox rows (spec 025). The mode is an
    /// operational cutover gate: flip to <see cref="SqlOwnedDueTime"/> only once
    /// no pre-upgrade dispatcher binary runs against the table, because an old
    /// dispatcher would eagerly broker-schedule new-style rows and could send a
    /// row after cancellation reported CancelledBeforeDispatch.
    /// </summary>
    public enum ScheduledDeliveryMode
    {
        /// <summary>
        /// Default: today's behavior, bit for bit — including CreatedAtUtc
        /// selection/ordering. Eager broker scheduling at dispatch, legacy
        /// Schedule returns 0, cancellation unsupported.
        /// </summary>
        BrokerScheduleAtDispatch = 0,

        /// <summary>
        /// New protocol: SQL owns the due time until it expires — due-time
        /// eligibility, claims, leases, session-head ordering, and
        /// handle-based cancellation with a pre-dispatch CAS fence.
        /// </summary>
        SqlOwnedDueTime = 1,
    }

    /// <summary>
    /// Configuration options for the SQL Server outbox.
    /// </summary>
    public class SqlServerOutboxOptions
    {
        /// <summary>
        /// Documented floor for the usable send window
        /// (<see cref="SendLeaseDuration"/> - <see cref="SendLeaseSafetyMargin"/>).
        /// Validation rejects configurations whose window is smaller, so a fence
        /// round trip can only consume the whole budget while the database is
        /// unhealthy — and the dispatcher then renews the lease instead of
        /// instantly timing out the send.
        /// </summary>
        public static readonly TimeSpan MinimumUsableSendWindow = IOutboxDispatchCoordinator.MinimumUsableSendWindow;

        /// <summary>
        /// The SQL Server connection string.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// The schema for the outbox table. Default: "nimbus".
        /// </summary>
        public string Schema { get; set; } = "nimbus";

        /// <summary>
        /// The name of the outbox table. Default: "OutboxMessages".
        /// </summary>
        public string TableName { get; set; } = "OutboxMessages";

        /// <summary>
        /// Whether to automatically create the outbox table on startup. Default: true.
        /// </summary>
        public bool AutoCreateTable { get; set; } = true;

        /// <summary>
        /// Scheduled-delivery mode. Default <see cref="ScheduledDeliveryMode.BrokerScheduleAtDispatch"/>
        /// (today's behavior, bit for bit). See the spec-025 cutover runbook before flipping.
        /// </summary>
        public ScheduledDeliveryMode ScheduledDelivery { get; set; } = ScheduledDeliveryMode.BrokerScheduleAtDispatch;

        /// <summary>
        /// Per-attempt send window: the dispatch-start fence extends the row's
        /// lease to SYSUTCDATETIME() + this value. Validated: positive, at most
        /// 24 hours, and leaving a usable window of at least
        /// <see cref="MinimumUsableSendWindow"/> after subtracting
        /// <see cref="SendLeaseSafetyMargin"/>.
        /// </summary>
        public TimeSpan SendLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Safety margin subtracted from the lease window when budgeting the
        /// bounded send. Validated: nonnegative, and
        /// <see cref="SendLeaseDuration"/> - margin &gt;= <see cref="MinimumUsableSendWindow"/>.
        /// </summary>
        public TimeSpan SendLeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(5);

        internal string FullTableName => $"[{Schema}].[{TableName}]";

        /// <summary>
        /// Validates the lease-option invariants (spec 025, revisions 5–6),
        /// throwing <see cref="ArgumentOutOfRangeException"/> naming the offending
        /// property. Called eagerly by <see cref="SqlServerOutbox"/>'s constructor
        /// and the AddNimBusSqlServerOutbox registration path so a
        /// misconfiguration fails fast at startup instead of silently cancelling
        /// every send attempt.
        /// </summary>
        public void ValidateLeaseOptions()
        {
            if (SendLeaseDuration <= TimeSpan.Zero || SendLeaseDuration > TimeSpan.FromHours(24))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SendLeaseDuration), SendLeaseDuration,
                    "SendLeaseDuration must be positive and at most 24 hours.");
            }

            if (SendLeaseSafetyMargin < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SendLeaseSafetyMargin), SendLeaseSafetyMargin,
                    "SendLeaseSafetyMargin must be nonnegative.");
            }

            if (SendLeaseDuration - SendLeaseSafetyMargin < MinimumUsableSendWindow)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SendLeaseSafetyMargin), SendLeaseSafetyMargin,
                    $"SendLeaseDuration - SendLeaseSafetyMargin must leave a usable send window of at least {MinimumUsableSendWindow} (got {SendLeaseDuration - SendLeaseSafetyMargin}).");
            }
        }
    }
}
