using System;
using System.Runtime.Serialization;

namespace NimBus.Core.Messages.Exceptions
{
    /// <summary>
    /// Signals that an operation failed with a transient error and SHOULD be
    /// retried by the transport. Promoted into <c>NimBus.Transport.Abstractions</c>
    /// because it appears in the <see cref="NimBus.Core.Messages.IMessageContext"/>
    /// signature; the namespace stays <c>NimBus.Core.Messages.Exceptions</c> with a
    /// <c>[TypeForwardedTo]</c> in <c>NimBus.Core</c> keeping existing
    /// <c>using</c> directives source-compatible.
    /// </summary>
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
}
