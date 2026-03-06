using System;
using System.Runtime.Serialization;

namespace NimBus.Core.Messages.Exceptions
{
    [Serializable]

    public class EventHandlerNotFoundException : Exception
    {
        public EventHandlerNotFoundException()
        {
        }

        public EventHandlerNotFoundException(string message) : base(message)
        {
        }

        public EventHandlerNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected EventHandlerNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

}
