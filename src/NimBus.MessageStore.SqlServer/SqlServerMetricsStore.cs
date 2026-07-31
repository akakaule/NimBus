using Dapper;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IMetricsStore"/>: server-side aggregation
/// over the Messages table. Carved out of <see cref="SqlServerMessageStore"/>, which
/// supplies connection/quoting/timeout via <see cref="SqlServerStoreContext"/>.
/// </summary>
internal sealed class SqlServerMetricsStore : IMetricsStore
{
    private readonly SqlServerStoreContext _ctx;

    public SqlServerMetricsStore(SqlServerStoreContext ctx)
    {
        _ctx = ctx;
    }

    private string T(string table) => _ctx.Table(table);

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
    {
        var sql = $@"
SELECT
    CASE
        WHEN MessageType = 'EventRequest' AND NULLIF(FromAddress, '') IS NOT NULL THEN FromAddress
        ELSE EndpointId
    END AS EndpointId,
    EventTypeId,
    MessageType,
    COUNT_BIG(*) AS EventCount
FROM {T("Messages")}
WHERE EnqueuedTimeUtc >= @From
GROUP BY
    CASE
        WHEN MessageType = 'EventRequest' AND NULLIF(FromAddress, '') IS NOT NULL THEN FromAddress
        ELSE EndpointId
    END,
    EventTypeId,
    MessageType";
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string EndpointId, string EventTypeId, string MessageType, long EventCount)>(
            sql,
            new { From = from },
            commandTimeout: _ctx.CommandTimeout);
        var result = new EndpointMetricsResult();
        foreach (var r in rows)
        {
            var bucket = r.MessageType switch
            {
                "EventRequest" => result.Published,
                "ResolutionResponse" => result.Handled,
                "ErrorResponse" => result.Failed,
                _ => null,
            };
            bucket?.Add(new EndpointEventTypeCount { EndpointId = r.EndpointId, EventTypeId = r.EventTypeId, Count = (int)r.EventCount });
        }
        return result;
    }

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from)
    {
        // Aggregate COUNT/AVG/MIN/MAX server-side and GROUP BY (endpoint, eventType)
        // so the Resolver hot path never streams every outcome row into memory.
        // COUNT/AVG/MIN/MAX ignore NULLs, replacing the old client-side null filter.
        var sql = $@"
SELECT EndpointId,
       EventTypeId,
       COUNT(QueueTimeMs) AS QueueCount,
       AVG(CAST(QueueTimeMs AS FLOAT)) AS QueueAvg,
       MIN(QueueTimeMs) AS QueueMin,
       MAX(QueueTimeMs) AS QueueMax,
       COUNT(ProcessingTimeMs) AS ProcessingCount,
       AVG(CAST(ProcessingTimeMs AS FLOAT)) AS ProcessingAvg,
       MIN(ProcessingTimeMs) AS ProcessingMin,
       MAX(ProcessingTimeMs) AS ProcessingMax
FROM {T("Messages")}
WHERE EnqueuedTimeUtc >= @From
  AND MessageType IN ('ResolutionResponse', 'ErrorResponse', 'SkipResponse', 'DeferralResponse', 'UnsupportedResponse')
  AND (QueueTimeMs IS NOT NULL OR ProcessingTimeMs IS NOT NULL)
GROUP BY EndpointId, EventTypeId";

        return GetEndpointLatencyMetricsCore(sql, from);
    }

    private async Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetricsCore(string sql, DateTime from)
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string EndpointId, string EventTypeId,
            int QueueCount, double? QueueAvg, long? QueueMin, long? QueueMax,
            int ProcessingCount, double? ProcessingAvg, long? ProcessingMin, long? ProcessingMax)>(
            sql,
            new { From = from },
            commandTimeout: _ctx.CommandTimeout);

        var latencies = rows
            .Select(r => new EndpointLatencyAggregate
            {
                EndpointId = r.EndpointId,
                EventTypeId = r.EventTypeId,
                Queue = BuildLatency(r.QueueCount, r.QueueAvg, r.QueueMin, r.QueueMax),
                Processing = BuildLatency(r.ProcessingCount, r.ProcessingAvg, r.ProcessingMin, r.ProcessingMax),
            })
            .ToList();

        return new EndpointLatencyMetricsResult { Latencies = latencies };
    }

    // A group whose column is entirely NULL yields COUNT = 0 with NULL avg/min/max;
    // collapse that to the zeroed aggregate the client-side path used to produce.
    private static LatencyAggregate BuildLatency(int count, double? avg, long? min, long? max)
        => count == 0
            ? new LatencyAggregate()
            : new LatencyAggregate
            {
                Count = count,
                AvgMs = avg ?? 0,
                MinMs = min ?? 0,
                MaxMs = max ?? 0,
            };

    public async Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from)
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<FailedMessageInfo>(
            $@"SELECT
                   EndpointId,
                   EventTypeId,
                   COALESCE(NULLIF(JSON_VALUE(MessageContentJson, '$.ErrorContent.ErrorText'), ''), DeadLetterErrorDescription, '') AS ErrorText,
                   EnqueuedTimeUtc,
                   EventId
               FROM {T("Messages")}
               WHERE MessageType = 'ErrorResponse'
                 AND EnqueuedTimeUtc >= @From",
            new { From = from }, commandTimeout: _ctx.CommandTimeout);
        return rows.ToList();
    }

    public async Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
    {
        // Floor to the bucket boundary server-side and GROUP BY, so we stop
        // streaming every message row. DATEADD(unit, DATEDIFF(unit, 0, ts), 0)
        // is version-agnostic (DATETRUNC is SQL Server 2022+). The unit is a
        // switch-constrained literal, never user input.
        var bucketUnit = substringLength switch
        {
            16 => "minute",
            13 => "hour",
            10 => "day",
            _ => "hour",
        };
        var bucketExpr = $"DATEADD({bucketUnit}, DATEDIFF({bucketUnit}, 0, EnqueuedTimeUtc), 0)";

        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string MessageType, DateTime Bucket, long Count)>(
            $@"SELECT MessageType, {bucketExpr} AS Bucket, COUNT_BIG(*) AS [Count]
               FROM {T("Messages")}
               WHERE EnqueuedTimeUtc >= @From
                 AND MessageType IN ('EventRequest', 'ResolutionResponse', 'ErrorResponse')
               GROUP BY MessageType, {bucketExpr}",
            new { From = from },
            commandTimeout: _ctx.CommandTimeout);

        var buckets = GenerateBucketKeys(from, DateTime.UtcNow, substringLength)
            .ToDictionary(k => k, k => new TimeSeriesBucket { Timestamp = k });

        foreach (var row in rows)
        {
            // The bucket start truncated to substringLength yields the same key
            // the per-row path produced (flooring == string truncation here).
            var key = DateTime.SpecifyKind(row.Bucket, DateTimeKind.Utc).ToString("o")[..substringLength];
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TimeSeriesBucket { Timestamp = key };
                buckets[key] = bucket;
            }

            switch (row.MessageType)
            {
                case "EventRequest":
                    bucket.Published += (int)row.Count;
                    break;
                case "ResolutionResponse":
                    bucket.Handled += (int)row.Count;
                    break;
                case "ErrorResponse":
                    bucket.Failed += (int)row.Count;
                    break;
            }
        }

        return new TimeSeriesResult
        {
            BucketSize = bucketLabel,
            DataPoints = buckets.Values.OrderBy(b => b.Timestamp).ToList(),
        };
    }

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
        var current = substringLength switch
        {
            16 => new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, DateTimeKind.Utc),
            13 => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc),
            10 => new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc)
        };

        var step = substringLength switch
        {
            16 => TimeSpan.FromMinutes(1),
            13 => TimeSpan.FromHours(1),
            10 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

        var keys = new List<string>();
        while (current <= to)
        {
            keys.Add(current.ToString("o")[..substringLength]);
            current += step;
        }

        return keys;
    }
}
