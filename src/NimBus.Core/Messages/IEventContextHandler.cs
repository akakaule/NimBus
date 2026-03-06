using NimBus.Core.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface IEventContextHandler
    {
        Task Handle(IMessageContext context, ILogger logger, CancellationToken cancellationToken = default);
    }
}