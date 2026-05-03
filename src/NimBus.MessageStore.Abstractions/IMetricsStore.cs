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
public interface IMetricsStore
{
    Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from);
    Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from);
    Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from);
    Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel);
}
