using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Shared Monitor acknowledgements: owns the expiry and clear-on-recovery policy so
/// every Monitor client sees the same silenced endpoints. Authorization and endpoint
/// validation are the caller's concern; endpoint ids passed here are canonical.
/// </summary>
public interface IMonitorAcknowledgementService
{
    /// <summary>
    /// Every acknowledgement still in force. Expired ones, and ones whose endpoint has
    /// recovered since they were placed, are removed from the store before returning.
    /// </summary>
    Task<IReadOnlyList<EndpointAcknowledgement>> GetActiveAsync();

    /// <summary>
    /// Acknowledges the endpoint's current failures, replacing any existing
    /// acknowledgement. The failed count is read live, not from the status cache.
    /// </summary>
    /// <param name="endpointId">Canonical endpoint id.</param>
    /// <param name="reason">Optional reason; trimmed, at most <see cref="MonitorAcknowledgementService.MaxReasonLength"/> characters.</param>
    /// <param name="acknowledgedBy">Display name or email of the operator, when known.</param>
    Task<EndpointAcknowledgement> AcknowledgeAsync(string endpointId, string? reason, string? acknowledgedBy);

    /// <summary>Removes the endpoint's acknowledgement, if any.</summary>
    /// <param name="endpointId">Canonical endpoint id.</param>
    Task ClearAsync(string endpointId);
}
