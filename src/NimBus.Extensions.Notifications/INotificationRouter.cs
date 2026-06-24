using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Routes a <see cref="Notification"/> to the registered channels, applying per-channel
    /// severity filtering, deduplication, and (optional) rate limiting.
    /// </summary>
    public interface INotificationRouter
    {
        /// <summary>
        /// Routes the notification to every eligible channel.
        /// </summary>
        Task RouteAsync(Notification notification, CancellationToken cancellationToken = default);
    }
}
