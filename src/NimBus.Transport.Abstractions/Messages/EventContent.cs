namespace NimBus.Core.Messages
{
    /// <summary>
    /// Event payload carried inside <see cref="MessageContent"/>. Promoted into
    /// <c>NimBus.Transport.Abstractions</c> for transport-neutral access; the
    /// namespace remains <c>NimBus.Core.Messages</c> and a <c>[TypeForwardedTo]</c>
    /// in <c>NimBus.Core</c> preserves source compatibility for existing consumers.
    /// </summary>
    public class EventContent
    {
        public string EventTypeId { get; set; }
        public string EventJson { get; set; }
    }
}
