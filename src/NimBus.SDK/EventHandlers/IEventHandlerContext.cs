using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.SDK.EventHandlers
{
    public interface IEventHandlerContext
    {   /// <summary>
        /// The Id of the currently processed message.
        /// </summary>
        string MessageId { get; }

        string EventId { get; }

        string EventType { get; }

        string CorrelationId { get; }
    }

    public class EventHandlerContext : IEventHandlerContext
    {   /// <summary>
        /// The Id of the currently processed message.
        /// </summary>
        public string MessageId { get; set; }

        public string EventId { get; set; }

        public string EventType { get; set; }

        public string CorrelationId { get; set; }
    }
}
