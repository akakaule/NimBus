using System.Diagnostics;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Reads / writes the W3C <c>traceparent</c> and <c>tracestate</c> properties on
/// transport message headers. Both transports use the same header names — there
/// is no transport-specific propagation format. Public so transport providers
/// can use the same propagation contract.
/// </summary>
public static class W3CMessagePropagator
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    /// <summary>
    /// Captures the current activity's W3C trace context. Returns <c>null</c> in both
    /// fields when no activity is current.
    /// </summary>
    public static (string? TraceParent, string? TraceState) Capture(Activity? activity)
    {
        if (activity is null)
            return (null, null);
        return (activity.Id, activity.TraceStateString);
    }

    /// <summary>
    /// Captures <see cref="Activity.Current"/>. Convenience overload.
    /// </summary>
    public static (string? TraceParent, string? TraceState) CaptureCurrent()
        => Capture(Activity.Current);

    /// <summary>
    /// Parses a persisted <c>traceparent</c> / <c>tracestate</c> pair into an
    /// <see cref="ActivityContext"/>. Returns <c>default</c> when the input is
    /// missing or malformed; callers should not treat that as failure — it just
    /// means the resulting span has no parent.
    /// </summary>
    public static ActivityContext TryParse(string? traceParent, string? traceState)
    {
        if (string.IsNullOrEmpty(traceParent))
            return default;

        return ActivityContext.TryParse(traceParent, traceState, out var context)
            ? context
            : default;
    }
}
