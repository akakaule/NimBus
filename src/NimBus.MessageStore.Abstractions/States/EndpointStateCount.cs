using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States
{
    public class EndpointStateCount
    {
        public string EndpointId { get; set; }
        public int FailedCount { get; set; }
        public int DeferredCount { get; set; }
        public int PendingCount { get; set; }
        public int DeadletterCount { get; set; }
        public int UnsupportedCount { get; set; }
        public DateTime EventTime { get; set; }
    }
}
