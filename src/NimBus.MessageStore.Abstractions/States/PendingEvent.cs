using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States
{
    public class PendingMessageEvent
    {
        public string OriginatingId { get; set; }
        public string EventId { get; set; }
        public string Status { get; set; }
    }
}
