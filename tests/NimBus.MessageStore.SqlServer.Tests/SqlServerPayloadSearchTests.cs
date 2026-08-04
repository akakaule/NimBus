#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// SQL Server payload search (GetEventsByFilter with <c>Payload</c>) must treat
/// the user's term as a literal substring — LIKE metacharacters (<c>% _ [ \</c>)
/// in the term must not act as wildcards (GH#92). Provider-specific rather than
/// conformance-based because the in-memory store does not implement the Payload
/// filter. Skipped automatically when <c>NIMBUS_SQL_TEST_CONNECTION</c> is unset.
/// </summary>
[TestClass]
public sealed class SqlServerPayloadSearchTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerPayloadSearchTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerPayloadSearchTests));

    private static INimBusMessageStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerPayloadSearchTests));

    private static async Task<string> SeedAsync(
        INimBusMessageStore store, string endpointId, string eventId, string payloadText)
    {
        var evt = new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = "s1",
            EndpointId = endpointId,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
            UpdatedAt = DateTime.UtcNow,
            CorrelationId = "corr-1",
            EndpointRole = EndpointRole.Subscriber,
            MessageType = MessageType.EventRequest,
            EventTypeId = "PayloadTest",
            To = endpointId,
            From = "publisher",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "PayloadTest", EventJson = payloadText },
            },
        };
        await store.UploadFailedMessage(eventId, "s1", endpointId, evt);
        return eventId;
    }

    private static async Task<string[]> SearchAsync(INimBusMessageStore store, string endpointId, string term)
    {
        var resp = await store.GetEventsByFilter(
            new EventFilter { EndPointId = endpointId, Payload = term }, null!, 50);
        return resp.Events.Select(e => e.EventId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string Scope(string value) => $"{value}-{Guid.NewGuid():N}"[..24];

    [TestMethod]
    public async Task Payload_percent_matches_literal_only()
    {
        var store = CreateStore();
        var endpointId = Scope("ep-pct");
        var literal = await SeedAsync(store, endpointId, Scope("e-lit"), "discount 100% applied");
        await SeedAsync(store, endpointId, Scope("e-plain"), "100 units shipped");

        var matches = await SearchAsync(store, endpointId, "100%");

        CollectionAssert.AreEqual(new[] { literal }, matches,
            "Term '100%' must match only the payload containing a literal '100%', not any payload containing '100'.");
    }

    [TestMethod]
    public async Task Payload_underscore_matches_literal_only()
    {
        var store = CreateStore();
        var endpointId = Scope("ep-us");
        var literal = await SeedAsync(store, endpointId, Scope("e-lit"), "key a_b value");
        await SeedAsync(store, endpointId, Scope("e-other"), "key axb value");

        var matches = await SearchAsync(store, endpointId, "a_b");

        CollectionAssert.AreEqual(new[] { literal }, matches,
            "Term 'a_b' must match only a literal underscore, not any single character.");
    }

    [TestMethod]
    public async Task Payload_bracket_matches_literal()
    {
        var store = CreateStore();
        var endpointId = Scope("ep-br");
        var literal = await SeedAsync(store, endpointId, Scope("e-lit"), "items[0] selected");

        var matches = await SearchAsync(store, endpointId, "[0]");

        CollectionAssert.AreEqual(new[] { literal }, matches,
            "Term '[0]' must match a literal bracket sequence (no character-class semantics, no error).");
    }

    [TestMethod]
    public async Task Payload_backslash_matches_literal()
    {
        var store = CreateStore();
        var endpointId = Scope("ep-bs");
        // EventJson containing "name" quotes: serialized MessageContentJson contains the
        // two-character sequences \" around name, so the term below exists literally.
        var literal = await SeedAsync(store, endpointId, Scope("e-lit"), "{\"name\":\"x\"}");

        var matches = await SearchAsync(store, endpointId, "\\\"name\\\"");

        CollectionAssert.AreEqual(new[] { literal }, matches,
            "A term containing backslashes must match the literal backslash sequence in the stored JSON.");
    }

    [TestMethod]
    public async Task Payload_plain_term_matches_mid_string()
    {
        var store = CreateStore();
        var endpointId = Scope("ep-mid");
        var literal = await SeedAsync(store, endpointId, Scope("e-lit"), "the needle-7f3 sits mid-payload");

        var matches = await SearchAsync(store, endpointId, "needle-7f3");

        CollectionAssert.AreEqual(new[] { literal }, matches,
            "A plain term must still match anywhere in MessageContentJson (contains, not prefix, semantics).");
    }
}
