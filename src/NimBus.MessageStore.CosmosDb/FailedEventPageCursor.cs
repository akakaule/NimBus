using System.Text;
using Newtonsoft.Json;

namespace NimBus.MessageStore;

/// <summary>
/// Continuation state for the cross-endpoint failure search. Cosmos keeps one container per
/// endpoint, so a page is a k-way merge of one ordered stream per container. Each stream is
/// resumed from its own Cosmos continuation token plus the number of rows of that Cosmos page
/// already handed out, so no row is skipped or repeated across pages even though Cosmos orders
/// the ISO-string <c>UpdatedAt</c> lexically (which is not chronological within a second).
/// </summary>
internal sealed class FailedEventPageCursor
{
    /// <summary>Position of each endpoint's stream; an endpoint absent from the map starts fresh.</summary>
    public Dictionary<string, SourcePosition> Sources { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Where one endpoint's stream resumes.</summary>
    /// <param name="Token">Cosmos continuation token of the page holding the next row; null for the first page.</param>
    /// <param name="Skip">Rows of that Cosmos page already handed out.</param>
    /// <param name="Done">True when the stream is exhausted.</param>
    public sealed record SourcePosition(string? Token, int Skip, bool Done);

    /// <summary>Position of an endpoint's stream, or the start when it has none yet.</summary>
    public SourcePosition PositionOf(string endpointId) =>
        Sources.TryGetValue(endpointId, out var position) ? position : new SourcePosition(null, 0, false);

    /// <summary>Serializes the cursor as an opaque continuation token.</summary>
    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Sources)));

    /// <summary>Parses a continuation token; an empty cursor for an empty or malformed token (first page).</summary>
    public static FailedEventPageCursor Decode(string? token)
    {
        if (string.IsNullOrEmpty(token)) return new FailedEventPageCursor();
        try
        {
            var sources = JsonConvert.DeserializeObject<Dictionary<string, SourcePosition>>(
                Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return sources == null
                ? new FailedEventPageCursor()
                : new FailedEventPageCursor { Sources = new Dictionary<string, SourcePosition>(sources, StringComparer.Ordinal) };
        }
        catch (FormatException)
        {
            return new FailedEventPageCursor();
        }
        catch (JsonException)
        {
            return new FailedEventPageCursor();
        }
    }

    /// <summary>
    /// Merges per-endpoint buffers into one page, newest first. Each buffer must hold its
    /// stream's next rows in the stream's own order and at least <paramref name="pageSize"/>
    /// rows unless the stream is exhausted. Returns the page and how many rows of each buffer
    /// it consumed, plus whether any row remains after the page.
    /// </summary>
    public static (List<T> Page, Dictionary<string, int> Consumed, bool HasMore) Merge<T>(
        IReadOnlyList<SourceBuffer<T>> sources,
        int pageSize)
    {
        var heads = new int[sources.Count];
        var page = new List<T>(pageSize);
        while (page.Count < pageSize)
        {
            var best = -1;
            for (var i = 0; i < sources.Count; i++)
            {
                if (heads[i] >= sources[i].Rows.Count) continue;
                if (best < 0 || SortsBefore(sources[i].Rows[heads[i]], sources[i].EndpointId, sources[best].Rows[heads[best]], sources[best].EndpointId))
                    best = i;
            }

            if (best < 0) break;
            page.Add(sources[best].Rows[heads[best]].Item);
            heads[best]++;
        }

        var consumed = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasMore = false;
        for (var i = 0; i < sources.Count; i++)
        {
            consumed[sources[i].EndpointId] = heads[i];
            if (heads[i] < sources[i].Rows.Count || !sources[i].Exhausted) hasMore = true;
        }

        return (page, consumed, hasMore);
    }

    // Newest first; ties broken by endpoint then row id so the merge is deterministic.
    private static bool SortsBefore<T>(BufferedRow<T> a, string endpointA, BufferedRow<T> b, string endpointB)
    {
        if (a.UpdatedAt.Ticks != b.UpdatedAt.Ticks) return a.UpdatedAt.Ticks > b.UpdatedAt.Ticks;
        var byEndpoint = string.CompareOrdinal(endpointA, endpointB);
        if (byEndpoint != 0) return byEndpoint > 0;
        return string.CompareOrdinal(a.Id, b.Id) > 0;
    }
}

/// <summary>A row read from one endpoint's stream, with where it sits in the Cosmos paging.</summary>
/// <param name="UpdatedAt">The row's <c>UpdatedAt</c>.</param>
/// <param name="Id">The document id.</param>
/// <param name="PageToken">Continuation token of the Cosmos page the row came from.</param>
/// <param name="IndexInPage">The row's index within that Cosmos page.</param>
/// <param name="Item">The row.</param>
internal sealed record BufferedRow<T>(DateTime UpdatedAt, string Id, string? PageToken, int IndexInPage, T Item);

/// <summary>The rows read ahead from one endpoint's stream for one page.</summary>
/// <param name="EndpointId">The endpoint.</param>
/// <param name="Rows">Rows in the stream's order.</param>
/// <param name="Exhausted">True when the stream has no rows beyond <paramref name="Rows"/>.</param>
/// <param name="NextPageToken">Continuation token after the last Cosmos page read.</param>
internal sealed record SourceBuffer<T>(string EndpointId, IReadOnlyList<BufferedRow<T>> Rows, bool Exhausted, string? NextPageToken)
{
    /// <summary>Where the stream resumes after <paramref name="consumed"/> of its rows were handed out.</summary>
    public FailedEventPageCursor.SourcePosition PositionAfter(int consumed)
    {
        if (consumed < Rows.Count)
            return new FailedEventPageCursor.SourcePosition(Rows[consumed].PageToken, Rows[consumed].IndexInPage, false);
        return Exhausted
            ? new FailedEventPageCursor.SourcePosition(null, 0, true)
            : new FailedEventPageCursor.SourcePosition(NextPageToken, 0, false);
    }
}
