namespace NimBus.Core.Messages
{
    /// <summary>
    /// Wire-protocol payload envelope carried on every <see cref="IMessage"/>:
    /// either an <see cref="EventContent"/> (success path) or an
    /// <see cref="ErrorContent"/> (failure path). Promoted into
    /// <c>NimBus.Transport.Abstractions</c> so transport adapters can read and
    /// route on the payload shape without depending on <c>NimBus.Core</c>; the
    /// namespace stays <c>NimBus.Core.Messages</c> for source-compatibility via
    /// <c>[TypeForwardedTo]</c> in <c>NimBus.Core</c>.
    /// </summary>
    public class MessageContent
    {
        public EventContent EventContent { get; set; }
        public ErrorContent ErrorContent { get; set; }
    }
}
