using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore
{
    public class MessageAuditEntity
    {
        public string AuditorName { get; set; }
        public DateTime AuditTimestamp { get; set; }
        public MessageAuditType AuditType { get; set; }
        public string? Comment { get; set; }
    }

    public enum MessageAuditType
    {
        Resubmit,
        ResubmitWithChanges,
        Skip,
        Retry,
        Comment,

        // Park-and-replay audit types (transport-agnostic deferred-by-session).
        // Emitted identically across transports — see
        // docs/specs/003-rabbitmq-transport/deferred-by-session-design.md §7.
        Parked,
        ReplayStarted,
        Replayed,
        ReplayCompleted,
        ReplaySkippedByOperator,
        ReplayDeadLettered,
    }
}
