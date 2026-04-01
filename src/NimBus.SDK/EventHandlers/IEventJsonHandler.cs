using NimBus.Core.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{
    public interface IEventJsonHandler
    {
        Task Handle(IMessageContext context, CancellationToken cancellationToken = default);
    }
}
