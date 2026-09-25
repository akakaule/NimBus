using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.MessageStore.States;

/// <summary>
/// Result of <see cref="Abstractions.IMessageTrackingStore.GetFailedEventHistogram"/>: sparse
/// per-bucket, per-endpoint, per-status counts of unresolved failures.
/// </summary>
public sealed class FailedEventHistogram
{
    /// <summary>Non-empty buckets, in no guaranteed order.</summary>
    public IReadOnlyList<FailedEventHistogramRow> Rows { get; set; } = Array.Empty<FailedEventHistogramRow>();

    /// <summary>
    /// True when the provider stopped counting at its row cap, so the counts are a lower bound.
    /// </summary>
    public bool Truncated { get; set; }
}

/// <summary>One non-empty histogram cell.</summary>
public sealed class FailedEventHistogramRow
{
    /// <summary>Inclusive start of the bucket, UTC; <c>fromUtc + k * bucketSize</c>.</summary>
    public DateTime BucketStartUtc { get; set; }

    /// <summary>Endpoint the events are on.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary><c>Failed</c>, <c>DeadLettered</c> or <c>Unsupported</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Number of events in the cell.</summary>
    public int Count { get; set; }
}

/// <summary>
/// Shared rules for the cross-endpoint failure queries, so every provider resolves statuses
/// and buckets identically.
/// </summary>
public static class FailedEventQuery
{
    /// <summary>The resolution statuses that count as an unresolved failure.</summary>
    public static readonly IReadOnlyList<string> Statuses = new[]
    {
        nameof(ResolutionStatus.Failed),
        nameof(ResolutionStatus.DeadLettered),
        nameof(ResolutionStatus.Unsupported),
    };

    /// <summary>
    /// The failure statuses to query: <paramref name="requested"/> intersected with
    /// <see cref="Statuses"/>, or all of them when nothing is requested. A request naming only
    /// non-failure statuses resolves to an empty list (no rows).
    /// </summary>
    public static List<string> ResolveStatuses(IReadOnlyCollection<string>? requested)
    {
        if (requested == null || requested.Count == 0)
            return Statuses.ToList();
        return Statuses.Where(s => requested.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Start of the bucket holding <paramref name="timestampUtc"/>, for buckets of
    /// <paramref name="bucketSize"/> aligned to <paramref name="fromUtc"/>.
    /// </summary>
    public static DateTime BucketStart(DateTime fromUtc, TimeSpan bucketSize, DateTime timestampUtc)
    {
        if (bucketSize <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucketSize), "Bucket size must be positive.");
        var index = (timestampUtc.Ticks - fromUtc.Ticks) / bucketSize.Ticks;
        return new DateTime(fromUtc.Ticks + index * bucketSize.Ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Folds individual (timestamp, endpoint, status) observations into sparse histogram rows.
    /// Used by providers that bucket in the application rather than in the query.
    /// </summary>
    public static List<FailedEventHistogramRow> Bucket(
        IEnumerable<(DateTime UpdatedAtUtc, string EndpointId, string Status)> observations,
        DateTime fromUtc,
        TimeSpan bucketSize) =>
        observations
            .GroupBy(o => (Start: BucketStart(fromUtc, bucketSize, o.UpdatedAtUtc), o.EndpointId, o.Status))
            .Select(g => new FailedEventHistogramRow
            {
                BucketStartUtc = g.Key.Start,
                EndpointId = g.Key.EndpointId,
                Status = g.Key.Status,
                Count = g.Count(),
            })
            .ToList();
}
