using System;

namespace NimBus.Core.Messages
{
    [Serializable]
    public class EventContextHandlerException : Exception
    {
        public EventContextHandlerException(Exception innerException) : base(innerException.Message, innerException)
        {
        }
    }
}