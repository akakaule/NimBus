using System;

namespace NimBus.Core.Messages.Exceptions
{
    public class UnsupportedMessageTypeException : Exception
    {
        public UnsupportedMessageTypeException(MessageType messageType) : base($"Unsupported MessageType: '{messageType}'.")
        {
        }
    }
}