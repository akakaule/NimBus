#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Spec 032: the conditional terminal write. The conformance suite proves the outcomes against a
/// live emulator; these tests prove the wire mechanics — the point read, the ETag precondition, the
/// document the repair writes, and how 404 / 412 / a mismatched last message id are handled.
/// </summary>
[TestClass]
public sealed class CosmosDbClientTryCompleteTests
{
    private const string EndpointId = "endpoint-1";
    private const string EventId = "event-1";
    private const string SessionId = "session-1";
    private const string RowId = $"{EventId}_{SessionId}";

    [TestMethod]
    public async Task A_matching_pending_row_is_replaced_conditionally_on_the_read_ETag()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-late"), "etag-9");
        var client = new CosmosDbClient(adapter);

        var applied = await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, "req-late", Completed("rsp-1"));

        Assert.IsTrue(applied);
        var options = container.CapturedRequestOptions.Single();
        Assert.IsNotNull(options);
        Assert.AreEqual("etag-9", options.IfMatchEtag, "the replace must be conditional on the row it was evaluated against");
        Assert.AreEqual(false, options.EnableContentResponseOnWrite);

        var document = container.SingleUpsertedDocument();
        Assert.AreEqual(RowId, document["id"]!.ToString());
        Assert.AreEqual("Completed", document["status"]!.ToString());
        Assert.AreEqual(true, document["deleted"]!.Value<bool>());
        Assert.AreEqual(60 * 60 * 24 * 30, document["ttl"]!.Value<int>(), "a repaired row expires on the same schedule as any Completed row");
        Assert.AreEqual("rsp-1", document["event"]!["LastMessageId"]!.ToString());
    }

    [TestMethod]
    public async Task A_row_with_another_last_message_id_is_refused_without_writing()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-other"), "etag-1");
        var client = new CosmosDbClient(adapter);

        var applied = await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, "req-late", Completed("rsp-1"));

        Assert.IsFalse(applied, "the row moved on since the preview: someone else decided");
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task An_already_terminal_row_is_refused_without_writing()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Completed", Row(MessageType.ResolutionResponse, "req-late"), "etag-1", deleted: true);
        var client = new CosmosDbClient(adapter);

        var applied = await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, "req-late", Completed("rsp-1"));

        Assert.IsFalse(applied);
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task A_missing_row_is_refused_and_never_created()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueReadFailure(RecordingCosmosContainerAdapter.NotFound());
        var client = new CosmosDbClient(adapter);

        var applied = await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, "req-late", Completed("rsp-1"));

        Assert.IsFalse(applied, "a repair only ever replaces a row that is there");
        Assert.AreEqual(0, container.UpsertedItems.Count);
        Assert.AreEqual(0, container.CreatedItems.Count);
    }

    [TestMethod]
    public async Task A_lost_compare_and_swap_is_refused_and_not_retried()
    {
        // Unlike the guarded write, a lost race is not transient: the row changed under the
        // operator's preview, so the answer is "re-preview", not "try again".
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-late"), "etag-1");
        container.ScriptedUpsertFailures.Enqueue(RecordingCosmosContainerAdapter.PreconditionFailed());
        var client = new CosmosDbClient(adapter);

        var applied = await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, "req-late", Completed("rsp-1"));

        Assert.IsFalse(applied);
        Assert.AreEqual(1, container.ReadCount, "no retry: one read, one attempt");
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task A_null_expected_id_matches_only_a_row_without_one()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, null), "etag-1");
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-late"), "etag-2");
        var client = new CosmosDbClient(adapter);

        Assert.IsTrue(await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, null, Completed("rsp-1")));
        Assert.IsFalse(await client.TryCompletePendingMessage(
            EventId, SessionId, EndpointId, null, Completed("rsp-2")));
    }

    private static UnresolvedEvent Row(MessageType messageType, string lastMessageId) => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        EndpointId = EndpointId,
        EventTypeId = "OrderPlaced",
        MessageType = messageType,
        LastMessageId = lastMessageId,
    };

    private static UnresolvedEvent Completed(string responseMessageId)
    {
        var projection = Row(MessageType.ResolutionResponse, responseMessageId);
        projection.ResolutionStatus = ResolutionStatus.Completed;
        return projection;
    }
}
