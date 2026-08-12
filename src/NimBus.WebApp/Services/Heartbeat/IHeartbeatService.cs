using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Platform heartbeat: the scheduled probe of every catalog endpoint plus the
/// Resolver liveness probe, and the operator-facing reads behind Admin → Health.
/// </summary>
public interface IHeartbeatService
{
    /// <summary>The stored schedule, or defaults when nothing has been written.</summary>
    Task<HeartbeatSettings> GetSettingsAsync();

    /// <summary>
    /// Stores the schedule after clamping it, and returns what was stored.
    /// </summary>
    /// <param name="settings">The requested schedule.</param>
    Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings);

    /// <summary>One row per catalog endpoint, deduplicated and ordered by endpoint id.</summary>
    Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync();

    /// <summary>
    /// Settles every probe that has been pending longer than the configured
    /// timeout, for both endpoints and platform services. Returns how many rows
    /// were settled.
    /// </summary>
    Task<int> SweepTimeoutsAsync();

    /// <summary>
    /// Probes every endpoint that has not explicitly opted out. Returns how many
    /// probes were sent.
    /// </summary>
    /// <param name="force">
    /// True to fan out even when <see cref="HeartbeatSettings.Enabled"/> is false —
    /// what the operator's "Send now" and the claimed scheduled tick both do.
    /// </param>
    Task<int> SendHeartbeatsAsync(bool force = false);

    /// <summary>
    /// Includes or excludes one endpoint from the fan-out, creating its metadata
    /// record if it has none.
    /// </summary>
    /// <param name="endpointId">The endpoint to configure.</param>
    /// <param name="enabled">False excludes the endpoint; true opts it back in.</param>
    Task SetEndpointEnabledAsync(string endpointId, bool enabled);

    /// <summary>Liveness of the platform's own services (currently the Resolver).</summary>
    Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync();

    /// <summary>
    /// Claims and sends the Resolver liveness probe when one is due. Runs
    /// independently of the global <c>Enabled</c> switch, which gates only the
    /// per-endpoint fan-out. Returns true when a probe was sent.
    /// </summary>
    Task<bool> ProbeResolverAsync();

    /// <summary>
    /// One scheduled tick: sweep timeouts, probe the Resolver, then claim and run
    /// the endpoint fan-out if one is due. Returns true when the fan-out ran.
    /// </summary>
    /// <remarks>
    /// The whole tick lives here rather than in the hosted service because the
    /// message store is registered per request: the hosted service opens a scope
    /// and calls this once, so every step of a tick shares one store instance.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the sends.</param>
    Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default);
}
