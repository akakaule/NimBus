using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

/// <summary>
/// Sends request/reply responses back to the requesting endpoint's
/// <c>{endpoint}-reply</c> subscription. Implementations own the transport;
/// see the wire contract on <see cref="ReplyMessage"/> and <see cref="ReplyConstants"/>.
/// </summary>
public interface IReplyDispatcher
{
    /// <summary>Sends one reply to the address carried by <paramref name="reply"/>.</summary>
    /// <param name="reply">The reply to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReplyAsync(ReplyMessage reply, CancellationToken cancellationToken = default);
}
