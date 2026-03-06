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
    }

    public enum MessageAuditType
    {
        Resubmit,
        ResubmitWithChanges,
        Skip,
        Retry
    }
}
