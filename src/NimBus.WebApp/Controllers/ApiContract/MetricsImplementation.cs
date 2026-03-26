using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
            Published = result.Published.Select(p => new EndpointEventTypeMessageCount
            {
                EndpointId = p.EndpointId,
                EventTypeId = p.EventTypeId,
                Count = p.Count
            }).ToList(),
            Handled = result.Handled.Select(h => new EndpointEventTypeMessageCount
            {
                EndpointId = h.EndpointId,
                EventTypeId = h.EventTypeId,
                Count = h.Count
            }).ToList(),
            Failed = result.Failed.Select(f => new EndpointEventTypeMessageCount
            {
                EndpointId = f.EndpointId,
                EventTypeId = f.EventTypeId,
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

    public async Task<ActionResult<FailedInsightsOverview>> GetMetricsFailedInsightsAsync(Period period)
    {
        var from = DateTime.UtcNow - PeriodToTimeSpan(period);
        var messages = await _cosmosClient.GetFailedMessageInsights(from);

        var groups = messages
            .GroupBy(m => ExtractErrorCategory(m.ErrorText))
            .Select(g =>
            {
                var subGroups = g
                    .GroupBy(m => NormalizeErrorPattern(m.ErrorText))
                    .Select(sg => new ErrorSubGroup
                    {
                        NormalizedPattern = sg.Key,
                        Count = sg.Count(),
                        Endpoints = sg.Select(m => m.EndpointId).Where(e => e != null).Distinct().ToList(),
                        EventTypes = sg.Select(m => m.EventTypeId).Where(e => e != null).Distinct().ToList(),
                        LatestOccurrence = sg.Max(m => m.EnqueuedTimeUtc),
                        ExampleErrorText = sg.First().ErrorText
                    })
                    .OrderByDescending(sg => sg.Count)
                    .ToList();

                return new ErrorPatternGroup
                {
                    ErrorCategory = g.Key,
                    Count = g.Count(),
                    Endpoints = g.Select(m => m.EndpointId).Where(e => e != null).Distinct().ToList(),
                    EventTypes = g.Select(m => m.EventTypeId).Where(e => e != null).Distinct().ToList(),
                    LatestOccurrence = g.Max(m => m.EnqueuedTimeUtc),
                    ExampleErrorText = g.First().ErrorText,
                    SubGroups = subGroups
                };
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        return new FailedInsightsOverview
        {
            Groups = groups,
            TotalFailed = messages.Count
        };
    }

    public async Task<ActionResult<TimeSeriesOverview>> GetMetricsTimeseriesAsync(Period period)
    {
        var from = DateTime.UtcNow - PeriodToTimeSpan(period);
        var (substringLength, bucketLabel) = PeriodToBucketConfig(period);
        var result = await _cosmosClient.GetTimeSeriesMetrics(from, substringLength, bucketLabel);

        return new TimeSeriesOverview
        {
            BucketSize = result.BucketSize,
            DataPoints = result.DataPoints.Select(dp => new TimeSeriesDataPoint
            {
                Timestamp = dp.Timestamp,
                Published = dp.Published,
                Handled = dp.Handled,
                Failed = dp.Failed
            }).ToList()
        };
    }

    private static (int substringLength, string label) PeriodToBucketConfig(Period period) => period switch
    {
        Period._1h => (16, "minute"),
        Period._12h => (13, "hour"),
        Period._1d => (13, "hour"),
        Period._3d => (13, "hour"),
        Period._7d => (13, "hour"),
        Period._30d => (10, "day"),
        _ => (13, "hour")
    };

    internal static string ExtractErrorCategory(string errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return "Unknown";
        if (errorText.StartsWith("["))
        {
            var end = errorText.IndexOf(']');
            if (end > 0) return errorText[..(end + 1)];
        }
        var colon = errorText.IndexOf(':');
        if (colon > 0 && colon < 100) return errorText[..colon];
        return errorText.Length > 100 ? errorText[..100] : errorText;
    }

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex ActionSuffix = new(
        @"\.?\s*Action:.*$",
        RegexOptions.Compiled);

    internal static string NormalizeErrorPattern(string errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return "Unknown";
        var normalized = GuidPattern.Replace(errorText, "<id>");
        normalized = ActionSuffix.Replace(normalized, "");
        return normalized.TrimEnd(' ', '.');
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
