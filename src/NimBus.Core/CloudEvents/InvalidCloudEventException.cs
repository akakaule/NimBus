using System;

namespace NimBus.Core.CloudEvents
{
    /// <summary>
    /// Raised when an inbound message is identified as a CloudEvent but is missing
    /// or has an invalid required attribute (<c>id</c>, <c>source</c>, <c>type</c>,
    /// <c>specversion</c>), or its <c>type</c> maps to no registered event contract.
    /// The subscriber pipeline dead-letters such messages with the exception message
    /// as the inspectable reason.
    /// </summary>
    [Serializable]
    public sealed class InvalidCloudEventException : Exception
    {
        /// <summary>Creates a new <see cref="InvalidCloudEventException"/>.</summary>
        public InvalidCloudEventException(string message) : base(message)
        {
        }

        /// <summary>Creates a new <see cref="InvalidCloudEventException"/> with an inner exception.</summary>
        public InvalidCloudEventException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
