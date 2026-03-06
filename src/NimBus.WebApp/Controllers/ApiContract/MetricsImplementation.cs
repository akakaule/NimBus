using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services.ApplicationInsights;

namespace NimBus.WebApp.Controllers.ApiContract;

public class MetricsImplementation : IMetricsApiController
{
    private readonly ICosmosDbClient _cosmosClient;
    private readonly IApplicationInsightsService _appInsights;

    public MetricsImplementation(ICosmosDbClient cosmosClient, IApplicationInsightsService appInsights)
    {
        _cosmosClient = cosmosClient;
        _appInsights = appInsights;
    }

    public async Task<ActionResult<MetricsOverview>> GetMetricsOverviewAsync(Period period)
    {
        var from = DateTime.UtcNow - PeriodToTimeSpan(period);
        var result = await _cosmosClient.GetEndpointMetrics(from);

        return new MetricsOverview
        {
            Published = result.Published.Select(p => new EndpointMessageCount
            {
                EndpointId = p.EndpointId,
                Count = p.Count
            }).ToList(),
            Handled = result.Handled.Select(h => new EndpointEventTypeMessageCount
            {
                EndpointId = h.EndpointId,
                EventTypeId = h.EventTypeId,
                Count = h.Count
            }).ToList(),
            Failed = result.Failed.Select(f => new EndpointMessageCount
            {
                EndpointId = f.EndpointId,
                Count = f.Count
            }).ToList()
        };
    }

    public async Task<ActionResult<LatencyOverview>> GetMetricsLatencyAsync(Period period)
    {
        var timeSpan = PeriodToTimeSpan(period);
        var metrics = await _appInsights.GetLatencyMetrics(timeSpan);

        return new LatencyOverview
        {
            Latencies = metrics.Select(m => new EndpointLatency
            {
                EndpointId = m.Destination,
                EventTypeId = m.EventType,
                Count = m.Count,
                AvgLatencyMs = Math.Round(m.AvgMs, 1),
                P50LatencyMs = Math.Round(m.P50Ms, 1),
                P95LatencyMs = Math.Round(m.P95Ms, 1),
                P99LatencyMs = Math.Round(m.P99Ms, 1),
                MaxLatencyMs = Math.Round(m.MaxMs, 1),
            }).ToList()
        };
    }

    private static TimeSpan PeriodToTimeSpan(Period period) => period switch
    {
        Period._1h => TimeSpan.FromHours(1),
        Period._12h => TimeSpan.FromHours(12),
        Period._1d => TimeSpan.FromDays(1),
        Period._3d => TimeSpan.FromDays(3),
        Period._7d => TimeSpan.FromDays(7),
        Period._30d => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1)
    };
}
