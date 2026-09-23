#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class DeferredRecoveryTests
{
    [TestMethod]
    public async Task Skip_uses_replace_with_etag_preserves_identity_and_applies_terminal_retention()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container("crm");
        var row = Row();
        container.EnqueueRead("event_session", "Deferred", row, "version-7");
        var client = new CosmosDbClient(adapter);
        Assert.IsTrue(await client.TrySkipDeferredMessage("event", "session", "crm", "deferral", row.UpdatedAt));
        Assert.AreEqual("event_session", container.ReplacedIds.Single());
        Assert.AreEqual("version-7", container.CapturedRequestOptions.Single()!.IfMatchEtag);
        var document = container.SingleUpsertedDocument();
        Assert.AreEqual("Skipped", document["status"]!.ToString());
        Assert.AreEqual("deferral", document["event"]!["LastMessageId"]!.ToString());
        Assert.IsTrue(document["deleted"]!.Value<bool>());
        Assert.AreEqual(2592000, document["ttl"]!.Value<int>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.PreconditionFailed)]
    public async Task Deletion_or_update_between_read_and_replace_never_recreates_the_row(HttpStatusCode failure)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container("crm");
        var row = Row();
        container.EnqueueRead("event_session", "Deferred", row, "version-7");
        container.ScriptedUpsertFailures.Enqueue(new CosmosException("race", failure, 0, "test", 0));
        Assert.IsFalse(await new CosmosDbClient(adapter).TrySkipDeferredMessage("event", "session", "crm", "deferral", row.UpdatedAt));
        Assert.AreEqual(1, container.ReplacedIds.Count);
        Assert.AreEqual(0, container.CreatedItems.Count);
    }

    [TestMethod]
    public async Task Changed_timestamp_refuses_write()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container("crm");
        var row = Row();
        container.EnqueueRead("event_session", "Deferred", row, "version-7");
        Assert.IsFalse(await new CosmosDbClient(adapter).TrySkipDeferredMessage("event", "session", "crm", "deferral", row.UpdatedAt.AddTicks(-1)));
        Assert.AreEqual(0, container.ReplacedIds.Count);
    }

    private static UnresolvedEvent Row() => new()
    {
        EventId = "event", SessionId = "session", EndpointId = "crm", LastMessageId = "deferral",
        MessageType = MessageType.DeferralResponse, ResolutionStatus = ResolutionStatus.Deferred,
        UpdatedAt = DateTime.UtcNow.AddHours(-1),
    };
}
