using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Non-throwing accessors for message identity fields. Transport contexts such as the
    /// Service Bus <c>MessageContext</c> throw <see cref="InvalidMessageException"/> when a
    /// field is absent on the wire. Lifecycle notification, diagnostic logging, and inbox
    /// deduplication must keep processing such messages, so they read identity through these
    /// helpers instead of the throwing properties.
    /// </summary>
    public static class MessageContextIdentity
    {
        /// <summary>
        /// Gets the message identifier, or <see langword="null"/> when the context is
        /// <see langword="null"/> or the identifier is not defined on the message.
        /// </summary>
        /// <param name="context">The message context.</param>
        /// <returns>The message identifier, or <see langword="null"/> when unavailable.</returns>
        public static string? GetMessageIdOrDefault(this IMessageContext? context)
        {
            if (context is null) return null;
            try { return context.MessageId; }
            catch (InvalidMessageException) { return null; }
        }

        /// <summary>
        /// Gets the receiving endpoint identifier, or <see langword="null"/> when the context is
        /// <see langword="null"/> or the endpoint is not defined on the message.
        /// </summary>
        /// <param name="context">The message context.</param>
        /// <returns>The endpoint identifier, or <see langword="null"/> when unavailable.</returns>
        public static string? GetEndpointIdOrDefault(this IMessageContext? context)
        {
            if (context is null) return null;
            try { return context.To; }
            catch (InvalidMessageException) { return null; }
        }

        /// <summary>
        /// Gets the session identifier, or <see langword="null"/> when the context is
        /// <see langword="null"/> or the identifier is not defined on the message.
        /// </summary>
        /// <param name="context">The message context.</param>
        /// <returns>The session identifier, or <see langword="null"/> when unavailable.</returns>
        public static string? GetSessionIdOrDefault(this IMessageContext? context)
        {
            if (context is null) return null;
            try { return context.SessionId; }
            catch (InvalidMessageException) { return null; }
        }

        /// <summary>
        /// Gets the event identifier, or <see langword="null"/> when the context is
        /// <see langword="null"/> or the identifier is not defined on the message.
        /// </summary>
        /// <param name="context">The message context.</param>
        /// <returns>The event identifier, or <see langword="null"/> when unavailable.</returns>
        public static string? GetEventIdOrDefault(this IMessageContext? context)
        {
            if (context is null) return null;
            try { return context.EventId; }
            catch (InvalidMessageException) { return null; }
        }
    }
}
