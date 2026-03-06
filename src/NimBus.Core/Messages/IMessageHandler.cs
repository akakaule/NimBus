using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface IMessageHandler
    {
        Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default);
    }
}
