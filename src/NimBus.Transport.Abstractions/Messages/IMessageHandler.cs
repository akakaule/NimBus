using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Pipeline terminus contract: invoked once per received message after all
    /// pipeline behaviours have run. Promoted into
    /// <c>NimBus.Transport.Abstractions</c> with namespace preserved
    /// (<c>NimBus.Core.Messages</c>); a <c>[TypeForwardedTo]</c> in
    /// <c>NimBus.Core</c> keeps existing using-directives source-compatible.
    /// </summary>
    public interface IMessageHandler
    {
        Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default);
    }
}
