using System;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Raised by a channel when delivery fails (e.g. a non-success HTTP status). The router and
    /// lifecycle observer catch and log these so a failed delivery never affects message processing.
    /// </summary>
    public class NotificationDeliveryException : Exception
    {
        public NotificationDeliveryException()
        {
        }

        public NotificationDeliveryException(string message) : base(message)
        {
        }

        public NotificationDeliveryException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
