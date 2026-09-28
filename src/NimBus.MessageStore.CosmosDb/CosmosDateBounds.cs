namespace NimBus.MessageStore;

/// <summary>
/// Date range bounds for Cosmos DB queries. Cosmos keeps a <see cref="DateTime"/> as the
/// ISO-8601 string Json.NET writes, with trailing fractional zeros trimmed
/// (<c>"…T10:00:00Z"</c>, <c>"…T10:00:00.1Z"</c>), and compares those strings, so within one
/// second their order is not chronological: '.' sorts before 'Z', and <c>".15Z"</c> before
/// <c>".1Z"</c>. A whole-second bound one second outside the range differs from every value in
/// the range in the seconds field, where string order is chronological, so a query using it
/// keeps every row in the range. The caller then applies the exact bound to the parsed value.
/// Like the other providers, a bound's ticks are read as UTC.
/// </summary>
internal static class CosmosDateBounds
{
    /// <summary>
    /// The query bound for values at or after <paramref name="from"/>: the start of the second
    /// before the one holding it.
    /// </summary>
    public static DateTime WidenFrom(DateTime from) =>
        new(Math.Max(WholeSecondTicks(from) - TimeSpan.TicksPerSecond, 0), DateTimeKind.Utc);

    /// <summary>
    /// The query bound for values at or before <paramref name="to"/>: the start of the second
    /// after the one holding it.
    /// </summary>
    public static DateTime WidenTo(DateTime to) =>
        new(Math.Min(WholeSecondTicks(to) + TimeSpan.TicksPerSecond, DateTime.MaxValue.Ticks), DateTimeKind.Utc);

    private static long WholeSecondTicks(DateTime value) => value.Ticks - value.Ticks % TimeSpan.TicksPerSecond;
}
