using System;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>
/// An operator's acknowledgement of an endpoint's current failures, shared by every
/// Monitor client so an ACK placed on one device silences the wall everywhere.
/// At most one acknowledgement exists per endpoint; placing a new one replaces it.
/// </summary>
/// <remarks>
/// Expiry and clear-on-recovery are policy, applied by the WebApp; the store only
/// persists the record.
/// </remarks>
public class EndpointAcknowledgement
{
    /// <summary>The acknowledged endpoint. Also the document id / primary key.</summary>
    [JsonProperty(PropertyName = "id")]
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>
    /// Opaque token minted for each acknowledgement (a GUID). Lets a caller remove the
    /// record only if it is still the acknowledgement it read — see
    /// <see cref="Abstractions.IEndpointAcknowledgementStore.RemoveEndpointAcknowledgement"/>.
    /// </summary>
    public string AcknowledgementId { get; set; } = string.Empty;

    /// <summary>Operator-supplied reason; empty when none was given.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Display name or email of the operator who acknowledged; null when unknown.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>UTC time the acknowledgement was placed.</summary>
    public DateTime AcknowledgedAtUtc { get; set; }

    /// <summary>UTC time after which the acknowledgement no longer applies.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The endpoint's failed count (dead-lettered included) when it was acknowledged.
    /// A positive value lets the acknowledgement clear once the endpoint recovers.
    /// </summary>
    public int FailedCountAtAcknowledgement { get; set; }
}
