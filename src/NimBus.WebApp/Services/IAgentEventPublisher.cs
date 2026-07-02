using NimBus.Core.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services
{
    /// <summary>Publishes a pre-built, validated classless agent event onto the Agent Zone topic (spec 022).</summary>
    public interface IAgentEventPublisher
    {
        Task PublishAsync(IMessage message, CancellationToken cancellationToken = default);
    }
}
