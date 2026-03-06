using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public interface ISender
    {
        Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default);
        Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default);
    }
}
