using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Puts one heartbeat probe on a Service Bus topic.
/// </summary>
/// <remarks>
/// The WebApp is not a NimBus endpoint and has no <c>ISender</c>: probes go
/// straight to the target topic, the way
/// <see cref="AdminService.ReprocessDeferredAsync"/> sends its
/// <c>ProcessDeferredRequest</c>. The seam exists so the heartbeat service can be
/// tested without a namespace.
/// </remarks>
public interface IHeartbeatMessageSender
{
    /// <summary>Sends <paramref name="message"/> to <paramref name="topicName"/>.</summary>
    /// <param name="topicName">Target topic — an endpoint id, or the Resolver for a liveness probe.</param>
    /// <param name="message">The probe. Its <see cref="Message.SessionId"/> selects the session.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendAsync(string topicName, Message message, CancellationToken cancellationToken = default);
}
