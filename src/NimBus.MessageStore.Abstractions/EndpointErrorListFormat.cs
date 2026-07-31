using System.Collections.Generic;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Provider-neutral formatting for <c>IMessageTrackingStore.GetEndpointErrorList</c>:
/// the statuses that count as errors and the historical <c>"id1;id2;"</c> shape
/// (trailing separator included, empty string when there are none). Providers keep
/// their own id shapes; only the status set and separator format are shared.
/// </summary>
public static class EndpointErrorListFormat
{
    /// <summary>Status value marking a failed event.</summary>
    public const string FailedStatus = "Failed";

    /// <summary>Status value marking a deferred event.</summary>
    public const string DeferredStatus = "Deferred";

    /// <summary>Joins ids into the historical <c>"id1;id2;"</c> shape.</summary>
    public static string Format(IReadOnlyCollection<string> ids)
        => ids.Count == 0 ? string.Empty : string.Join(";", ids) + ";";
}
