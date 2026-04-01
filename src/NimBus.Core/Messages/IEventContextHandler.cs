using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface IEventContextHandler
    {
        Task Handle(IMessageContext context, CancellationToken cancellationToken = default);
    }
}
