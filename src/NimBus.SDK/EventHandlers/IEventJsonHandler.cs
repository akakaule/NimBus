using NimBus.Core.Messages;
using NimBus.Core.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{
    public interface IEventJsonHandler
    {
        Task Handle(IMessageContext context, ILogger logger, CancellationToken cancellationToken = default);
    }
}
