using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace NimBus.WebApp.Services.ApplicationInsights
{
    public class ApplicationInsightsService : IApplicationInsightsService
    {

        private HttpClient client;

        public ApplicationInsightsService(string applicationId, string apiKey)
        {
            this.client = new HttpClient() { BaseAddress = new Uri($"https://api.applicationinsights.io/v1/apps/{applicationId}/") };
            this.client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }

        public async Task<IEnumerable<LogEntry>> GetLogs(string messageId, SeverityLevel minimumLevel)
        {
            return await GetLogs(new Filter()
            {
                EventId = messageId,
                MinimumLogLevel = minimumLevel,
            });
        }

        public async Task<IEnumerable<LogEntry>> GetLogs(Filter filter)
        {
            var query = "traces " +
                    " | where itemType == 'trace' " +
                    (filter.Before == null ? "" : $"and timestamp <= datetime({filter.Before.Value.ToString("u")}) ") +
                    (filter.After == null ? "" : $"and timestamp >= datetime({filter.After.Value.ToString("u")}) ") +
                    //(filter.LogSource == null ? "" : $"and tostring(customDimensions['LogSource']) == '{filter.LogSource}' ") +
                    //(filter.EventType == null ? "" : $"and tostring(customDimensions['EventType']) == '{filter.EventType}' ") +
                    //(filter.CorrelationId == null ? "" : $"and tostring(customDimensions['CorrelationId']) == '{filter.CorrelationId}' ") +
                    (string.IsNullOrEmpty(filter.EventId) ? "" : $"and tostring(customDimensions['DIS.EventId']) == '{filter.EventId}' ") +
                    //(filter.PublishedBy == null ? "" : $"and tostring(customDimensions['PublishedBy']) == '{filter.PublishedBy.ToString()}' ") +
                    (filter.MinimumLogLevel == null ? "" : $"and severityLevel >= {(int)filter.MinimumLogLevel} ") +
                    " | top 1000 by timestamp desc";
            var req = $"query?query={HttpUtility.UrlEncode(query)}";

            using var response = await client.GetAsync(req);
            
            var result = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<AppInsightsResultRaw>(result);

            return new LogTraceCollection(obj).GetLogEntries();
        }

        public async Task<IEnumerable<LatencyMetric>> GetLatencyMetrics(TimeSpan period)
        {
            var periodKql = period.TotalHours switch
            {
                <= 1 => "1h",
                <= 12 => "12h",
                <= 24 => "1d",
                <= 72 => "3d",
                <= 168 => "7d",
                _ => "30d"
            };

            var query = "customMetrics" +
                $" | where name == 'dis.message.e2e_latency'" +
                $" | where timestamp >= ago({periodKql})" +
                " | extend eventType = tostring(customDimensions['messaging.event_type'])," +
                "          destination = tostring(customDimensions['messaging.destination'])" +
                " | summarize" +
                "     count_ = count()," +
                "     avg_ = avg(value)," +
                "     p50 = percentile(value, 50)," +
                "     p95 = percentile(value, 95)," +
                "     p99 = percentile(value, 99)," +
                "     max_ = max(value)" +
                "   by destination, eventType" +
                " | order by destination asc, eventType asc";

            var req = $"query?query={HttpUtility.UrlEncode(query)}";
            using var response = await client.GetAsync(req);
            var result = await response.Content.ReadAsStringAsync();
            var obj = JsonConvert.DeserializeObject<AppInsightsResultRaw>(result);

            var metrics = new List<LatencyMetric>();
            var table = obj?.Tables?.FirstOrDefault();
            if (table == null) return metrics;

            var colMap = new Dictionary<string, int>();
            for (var i = 0; i < table.Columns.Length; i++)
                colMap[table.Columns[i].Name] = i;

            foreach (var row in table.Rows)
            {
                metrics.Add(new LatencyMetric
                {
                    Destination = colMap.ContainsKey("destination") ? row[colMap["destination"]] : "",
                    EventType = colMap.ContainsKey("eventType") ? row[colMap["eventType"]] : "",
                    Count = colMap.ContainsKey("count_") ? int.TryParse(row[colMap["count_"]], out var c) ? c : 0 : 0,
                    AvgMs = colMap.ContainsKey("avg_") ? double.TryParse(row[colMap["avg_"]], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0 : 0,
                    P50Ms = colMap.ContainsKey("p50") ? double.TryParse(row[colMap["p50"]], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p50) ? p50 : 0 : 0,
                    P95Ms = colMap.ContainsKey("p95") ? double.TryParse(row[colMap["p95"]], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p95) ? p95 : 0 : 0,
                    P99Ms = colMap.ContainsKey("p99") ? double.TryParse(row[colMap["p99"]], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p99) ? p99 : 0 : 0,
                    MaxMs = colMap.ContainsKey("max_") ? double.TryParse(row[colMap["max_"]], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0 : 0,
                });
            }

            return metrics;
        }
    }

    public interface IApplicationInsightsService
    {
        Task<IEnumerable<LogEntry>> GetLogs(string messageId, SeverityLevel minimumLevel = SeverityLevel.Information);
        Task<IEnumerable<LogEntry>> GetLogs(Filter filter);
        Task<IEnumerable<LatencyMetric>> GetLatencyMetrics(TimeSpan period);
    }

    public class LatencyMetric
    {
        public string Destination { get; set; }
        public string EventType { get; set; }
        public int Count { get; set; }
        public double AvgMs { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double MaxMs { get; set; }
    }
}
