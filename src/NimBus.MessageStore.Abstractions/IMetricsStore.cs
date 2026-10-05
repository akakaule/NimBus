using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Aggregated metrics queries: endpoint throughput, latency, failed-message insights,
/// and time-series buckets. Implemented per storage provider; SQL providers may use
/// indexed views or precomputed tables behind these methods.
/// </summary>
/// <remarks>
/// Queries cover messages enqueued in <c>[from, to)</c>. The <c>from</c>-only members predate
/// the upper bound and cover <c>[from, now)</c>. The <c>(from, to)</c> members fall back to them
/// by default, ignoring <c>to</c>, so an implementation written against the older contract keeps
/// compiling; every NimBus provider overrides them.
/// </remarks>
public interface IMetricsStore
{
    /// <summary>Published, handled and failed counts per endpoint and event type since <paramref name="from"/>.</summary>
    [Obsolete("Use GetEndpointMetrics(from, to). This overload will be removed in v5.")]
    Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from);

    /// <summary>Queue and processing latency aggregates per endpoint and event type since <paramref name="from"/>.</summary>
    [Obsolete("Use GetEndpointLatencyMetrics(from, to). This overload will be removed in v5.")]
    Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from);

    /// <summary>Error details of the failed messages since <paramref name="from"/>.</summary>
    [Obsolete("Use GetFailedMessageInsights(from, to). This overload will be removed in v5.")]
    Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from);

    /// <summary>Published, handled and failed counts per time bucket, zero-filled from <paramref name="from"/> to now.</summary>
    [Obsolete("Use GetTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel);

    /// <summary>
    /// Time-series buckets of published (EventRequest) counts grouped by event
    /// type. Series are sorted by total desc; buckets are sparse (no zero-fill)
    /// and sorted ascending. Bucket key contract matches GetTimeSeriesMetrics.
    /// </summary>
    [Obsolete("Use GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel);

#pragma warning disable CS0618 // The defaults bridge to the obsolete overloads until v5.

    /// <summary>
    /// Published, handled and failed counts per endpoint and event type for messages enqueued in
    /// [<paramref name="from"/>, <paramref name="to"/>).
    /// </summary>
    Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to) =>
        GetEndpointMetrics(from);

    /// <summary>
    /// Queue and processing latency aggregates per endpoint and event type for messages enqueued in
    /// [<paramref name="from"/>, <paramref name="to"/>).
    /// </summary>
    Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from, DateTime to) =>
        GetEndpointLatencyMetrics(from);

    /// <summary>Error details of the failed messages enqueued in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to) =>
        GetFailedMessageInsights(from);

    /// <summary>
    /// Published, handled and failed counts per time bucket for messages enqueued in
    /// [<paramref name="from"/>, <paramref name="to"/>), zero-filled across that window. Bucket keys
    /// are the bucket start as ISO-8601 UTC truncated to <paramref name="substringLength"/>.
    /// </summary>
    Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel) =>
        GetTimeSeriesMetrics(from, substringLength, bucketLabel);

    /// <summary>
    /// Time-series buckets of published (EventRequest) counts grouped by event type for messages
    /// enqueued in [<paramref name="from"/>, <paramref name="to"/>). Series are sorted by total desc;
    /// buckets are sparse (no zero-fill) and sorted ascending. Bucket key contract matches
    /// <see cref="GetTimeSeriesMetrics(DateTime, DateTime, int, string)"/>.
    /// </summary>
    Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel) =>
        GetEventTypeTimeSeriesMetrics(from, substringLength, bucketLabel);

#pragma warning restore CS0618
}
