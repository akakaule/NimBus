#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The cross-endpoint failure search merges one ordered stream per endpoint container. These
/// tests pin the merge order and the resume positions, which the live conformance suite only
/// exercises with small pages.
/// </summary>
[TestClass]
public sealed class FailedEventPageCursorTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private static SourceBuffer<string> Source(string endpointId, bool exhausted, string? nextPageToken, params (int Seconds, string Id, string? PageToken, int Index)[] rows) =>
        new(endpointId,
            rows.Select(r => new BufferedRow<string>(T0.AddSeconds(r.Seconds), r.Id, r.PageToken, r.Index, r.Id)).ToList(),
            exhausted,
            nextPageToken);

    [TestMethod]
    public void Merge_takes_the_newest_head_across_sources()
    {
        var a = Source("a", true, null, (50, "a1", null, 0), (30, "a2", null, 1), (10, "a3", null, 2));
        var b = Source("b", true, null, (40, "b1", null, 0), (20, "b2", null, 1));

        var (page, consumed, hasMore) = FailedEventPageCursor.Merge(new[] { a, b }, 3);

        CollectionAssert.AreEqual(new[] { "a1", "b1", "a2" }, page);
        Assert.AreEqual(2, consumed["a"]);
        Assert.AreEqual(1, consumed["b"]);
        Assert.IsTrue(hasMore);
    }

    [TestMethod]
    public void Merge_reports_no_more_rows_when_every_source_is_drained_and_exhausted()
    {
        var a = Source("a", true, null, (50, "a1", null, 0));
        var b = Source("b", true, null, (40, "b1", null, 0));

        var (page, _, hasMore) = FailedEventPageCursor.Merge(new[] { a, b }, 5);

        Assert.AreEqual(2, page.Count);
        Assert.IsFalse(hasMore);
    }

    [TestMethod]
    public void Merge_keeps_going_when_a_drained_source_is_not_exhausted()
    {
        var a = Source("a", false, "after-a", (50, "a1", null, 0));

        var (_, _, hasMore) = FailedEventPageCursor.Merge(new[] { a }, 1);

        Assert.IsTrue(hasMore, "A source Cosmos has not exhausted may still hold rows.");
    }

    [TestMethod]
    public void Merge_breaks_timestamp_ties_by_endpoint_then_id()
    {
        var a = Source("a", true, null, (10, "x1", null, 0));
        var b = Source("b", true, null, (10, "x0", null, 0));

        var (page, _, _) = FailedEventPageCursor.Merge(new[] { a, b }, 2);

        CollectionAssert.AreEqual(new[] { "x0", "x1" }, page, "Equal timestamps sort by endpoint id descending.");
    }

    [TestMethod]
    public void PositionAfter_resumes_inside_the_cosmos_page_of_the_next_row()
    {
        var a = Source("a", false, "p3", (50, "a1", "p1", 4), (40, "a2", "p2", 0), (30, "a3", "p2", 1));

        Assert.AreEqual(new FailedEventPageCursor.SourcePosition("p1", 4, false), a.PositionAfter(0));
        Assert.AreEqual(new FailedEventPageCursor.SourcePosition("p2", 1, false), a.PositionAfter(2));
        Assert.AreEqual(new FailedEventPageCursor.SourcePosition("p3", 0, false), a.PositionAfter(3), "After the buffer, resume at the next Cosmos page.");
    }

    [TestMethod]
    public void PositionAfter_marks_a_drained_exhausted_source_done()
    {
        var a = Source("a", true, null, (50, "a1", null, 0));

        Assert.AreEqual(new FailedEventPageCursor.SourcePosition(null, 0, true), a.PositionAfter(1));
    }

    [TestMethod]
    public void Cursor_round_trips_through_its_token()
    {
        var cursor = new FailedEventPageCursor();
        cursor.Sources["CrmEndpoint"] = new FailedEventPageCursor.SourcePosition("{\"token\":\"abc\"}", 3, false);
        cursor.Sources["ErpEndpoint"] = new FailedEventPageCursor.SourcePosition(null, 0, true);

        var decoded = FailedEventPageCursor.Decode(cursor.Encode());

        Assert.AreEqual(cursor.Sources["CrmEndpoint"], decoded.PositionOf("CrmEndpoint"));
        Assert.AreEqual(cursor.Sources["ErpEndpoint"], decoded.PositionOf("ErpEndpoint"));
        Assert.AreEqual(new FailedEventPageCursor.SourcePosition(null, 0, false), decoded.PositionOf("NewEndpoint"), "An endpoint without a position starts at the beginning.");
    }

    [TestMethod]
    public void Decode_treats_a_malformed_token_as_the_first_page()
    {
        Assert.AreEqual(0, FailedEventPageCursor.Decode("not base64!").Sources.Count);
        Assert.AreEqual(0, FailedEventPageCursor.Decode(Convert.ToBase64String(new byte[] { 1, 2, 3 })).Sources.Count);
        Assert.AreEqual(0, FailedEventPageCursor.Decode(null).Sources.Count);
    }
}
