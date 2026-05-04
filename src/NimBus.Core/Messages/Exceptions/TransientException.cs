using System;
using System.Runtime.Serialization;

namespace NimBus.Core.Messages.Exceptions
{
    [Serializable]
    public class ThrottleException : Exception
    {
        public ThrottleException()
        {
        }

        public ThrottleException(string message) : base(message)
        {
        }

        public ThrottleException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected ThrottleException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
