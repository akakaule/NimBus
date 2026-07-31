#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

/// <summary>
/// Max-page-size clamp with real data volume. In-memory only: seeding 1001
/// documents against the live Cosmos/SQL emulators buys no extra coverage over
/// the conformance suite's 101-seed routing proof plus the Resolve() unit tests.
/// </summary>
[TestClass]
public class InMemoryPaginationCapTests
{
    [TestMethod]
    public async Task GetBlockedEventsOnSession_take_above_max_returns_max_page_size()
    {
        var store = new InMemoryMessageStore();
        const string endpointId = "ep-max-clamp";
        var seeded = PaginationLimits.MaxPageSize + 1;

        for (var i = 0; i < seeded; i++)
        {
            var id = $"clamp-{i}";
            await store.UploadPendingMessage(id, "session-clamp", endpointId, new NimBus.MessageStore.UnresolvedEvent
            {
                EventId = id,
                SessionId = "session-clamp",
                EndpointId = endpointId,
            });
        }

        var page = await store.GetBlockedEventsOnSession(endpointId, "session-clamp", 0, seeded);

        Assert.AreEqual(seeded, page.Total);
        Assert.AreEqual(PaginationLimits.MaxPageSize, page.Items.Count);
    }
}
