using System;

namespace AspirePubSub.WarehouseSubscriber.Handlers
{
    /// <summary>
    /// The unreliability this sample adapter deliberately exhibits.
    /// </summary>
    /// <remarks>
    /// The Warehouse adapter drops roughly three messages in ten so the sample
    /// produces a steady trickle of failures to look at: retries, the Failed
    /// column on the Endpoints page, resubmit and skip from the WebApp, and
    /// error grouping all need something to act on. Billing stays reliable, so
    /// the two endpoints read differently at a glance.
    /// </remarks>
    internal static class FlakyWarehouse
    {
        /// <summary>Share of messages that fail. 0.3 == 30%.</summary>
        public const double FailureRate = 0.3;

        /// <summary>
        /// True when this delivery should fail. Rolled per call, so a resubmitted
        /// message gets a fresh chance rather than failing forever — the point is
        /// intermittent failure an operator can clear, not a poison message.
        /// </summary>
        public static bool ShouldFail() => Random.Shared.NextDouble() < FailureRate;
    }
}
