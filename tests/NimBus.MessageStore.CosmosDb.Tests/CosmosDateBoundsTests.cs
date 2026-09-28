#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Cosmos compares DateTimes as the strings Json.NET writes. These tests replay that ordinal
/// comparison offline to pin that the widened query bounds never drop a value in range; the
/// live conformance suite covers the queries themselves.
/// </summary>
[TestClass]
public sealed class CosmosDateBoundsTests
{
    private static readonly DateTime Second = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private static readonly long[] Fractions =
    [
        0, 1, TimeSpan.TicksPerMillisecond, 100 * TimeSpan.TicksPerMillisecond,
        150 * TimeSpan.TicksPerMillisecond, 999 * TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond - 1,
    ];

    // The string a document holds for a DateTime: Json.NET's default, fractional zeros trimmed.
    private static string Stored(DateTime value) => JsonConvert.SerializeObject(value).Trim('"');

    // Values at a spread of fractions in the seconds around a bound.
    private static IEnumerable<DateTime> ValuesAround(DateTime bound) =>
        from seconds in Enumerable.Range(-2, 5)
        from fraction in Fractions
        select new DateTime(bound.Ticks - bound.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc)
            .AddSeconds(seconds)
            .AddTicks(fraction);

    [TestMethod]
    public void Stored_strings_do_not_sort_chronologically_within_a_second()
    {
        Assert.IsLessThan(0, string.CompareOrdinal(Stored(Second.AddMilliseconds(100)), Stored(Second)), "'.' sorts before 'Z'.");
        Assert.IsLessThan(0, string.CompareOrdinal(Stored(Second.AddMilliseconds(150)), Stored(Second.AddMilliseconds(100))), "\".15Z\" sorts before \".1Z\".");
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(100 * TimeSpan.TicksPerMillisecond)]
    [DataRow(999 * TimeSpan.TicksPerMillisecond)]
    [DataRow(TimeSpan.TicksPerSecond - 1)]
    public void Widened_bounds_keep_every_value_in_range_by_string_comparison(long boundFraction)
    {
        var bound = Second.AddTicks(boundFraction);
        var from = Stored(CosmosDateBounds.WidenFrom(bound));
        var to = Stored(CosmosDateBounds.WidenTo(bound));

        foreach (var value in ValuesAround(bound))
        {
            if (value >= bound)
                Assert.IsGreaterThanOrEqualTo(0, string.CompareOrdinal(Stored(value), from), $"{Stored(value)} is at or after {bound:O}.");
            if (value <= bound)
                Assert.IsLessThanOrEqualTo(0, string.CompareOrdinal(Stored(value), to), $"{Stored(value)} is at or before {bound:O}.");
        }
    }

    [TestMethod]
    public void Widened_bounds_are_whole_utc_seconds_outside_the_bounds_second()
    {
        var bound = Second.AddMilliseconds(250);

        Assert.AreEqual(Second.AddSeconds(-1), CosmosDateBounds.WidenFrom(bound));
        Assert.AreEqual(Second.AddSeconds(1), CosmosDateBounds.WidenTo(bound));
        Assert.AreEqual(DateTimeKind.Utc, CosmosDateBounds.WidenFrom(DateTime.SpecifyKind(bound, DateTimeKind.Unspecified)).Kind);
        Assert.AreEqual(Second.AddSeconds(-1), CosmosDateBounds.WidenFrom(Second), "A whole-second bound still moves out of its own second.");
        Assert.AreEqual(Second.AddSeconds(1), CosmosDateBounds.WidenTo(Second));
    }

    [TestMethod]
    public void Widened_bounds_clamp_at_the_ends_of_the_DateTime_range()
    {
        Assert.AreEqual(DateTime.MinValue, CosmosDateBounds.WidenFrom(DateTime.MinValue));
        Assert.AreEqual(DateTime.MaxValue, CosmosDateBounds.WidenTo(DateTime.MaxValue));
    }
}
