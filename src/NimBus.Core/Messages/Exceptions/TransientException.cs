using System;
using System.Runtime.Serialization;

namespace NimBus.Core.Messages.Exceptions
{
    [Serializable]
    public class TransientException : Exception
    {
        public TransientException()
        {
        }

        public TransientException(string message) : base(message)
        {
        }

        public TransientException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected TransientException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

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