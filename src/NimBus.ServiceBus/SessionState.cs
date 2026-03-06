using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core.Messages
{
    public class SessionState
    {
        public SessionState()
        {
            DeferredSequenceNumbers = new List<long>();
        }

        /// <summary>
        /// Legacy: Sequence numbers of deferred messages stored in session state.
        /// </summary>
        public List<long> DeferredSequenceNumbers { get; set; }

        public string BlockedByEventId { get; set; }

        /// <summary>
        /// Count of messages deferred to the separate deferred subscription (new approach).
        /// </summary>
        public int DeferredCount { get; set; }

        /// <summary>
        /// Next sequence number to assign for ordering deferred messages (new approach).
        /// </summary>
        public int NextDeferralSequence { get; set; }

        public bool IsEmpty() =>
            BlockedByEventId == null &&
            !DeferredSequenceNumbers.Any() &&
            DeferredCount == 0;

        /// <summary>
        /// Returns true if there are any deferred messages (legacy or new approach).
        /// </summary>
        public bool HasDeferredMessages() =>
            DeferredCount > 0 || DeferredSequenceNumbers?.Any() == true;
    }
}