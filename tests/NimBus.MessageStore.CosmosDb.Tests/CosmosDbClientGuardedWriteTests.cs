#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Spec 030: non-terminal writes of a guarded message type go through the read + ETag replace
/// path instead of the unconditional upsert. The conformance suite proves the rule's outcomes
/// against a live emulator; these tests prove the wire mechanics — which calls are made, with
/// which preconditions, and how 404 / 409 / 412 are handled.
/// </summary>
[TestClass]
public sealed class CosmosDbClientGuardedWriteTests
{
    private const string EndpointId = "endpoint-1";
    private const string EventId = "event-1";
    private const string SessionId = "session-1";
    private const string RowId = $"{EventId}_{SessionId}";

    [TestMethod]
    public async Task A_stale_request_copy_is_refused_without_writing()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Completed", Row(MessageType.ResolutionResponse, "rsp-1", parentMessageId: "req-1"), "etag-1", deleted: true);
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-late"));

        Assert.IsFalse(applied);
        Assert.AreEqual(0, container.UpsertedItems.Count, "a refused write must not touch the row");
        Assert.AreEqual(0, container.CreatedItems.Count);
    }

    [TestMethod]
    public async Task A_request_stage_replace_carries_the_read_ETag()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-1"), "etag-7");
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-2"));

        Assert.IsTrue(applied);
        var options = container.CapturedRequestOptions.Single();
        Assert.IsNotNull(options);
        Assert.AreEqual("etag-7", options.IfMatchEtag, "the replace must be conditional on the row it was evaluated against");
        Assert.AreEqual(false, options.EnableContentResponseOnWrite);
        Assert.AreEqual("req-2", container.SingleUpsertedDocument()["event"]!["LastMessageId"]!.ToString());
    }

    [TestMethod]
    public async Task A_missing_row_is_created()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueReadFailure(RecordingCosmosContainerAdapter.NotFound());
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-1"));

        Assert.IsTrue(applied);
        Assert.AreEqual(1, container.CreatedItems.Count, "the first projection of an event is a create, not a replace");
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task A_conflicting_create_is_re_read()
    {
        // Two instances racing the first write: the loser's create conflicts and it must
        // re-evaluate against the row that won, not give up.
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueReadFailure(RecordingCosmosContainerAdapter.NotFound());
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-1"), "etag-1");
        container.ScriptedCreateFailures.Enqueue(RecordingCosmosContainerAdapter.Conflict());
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-1"));

        Assert.IsTrue(applied);
        Assert.AreEqual(2, container.ReadCount);
        Assert.AreEqual("etag-1", container.CapturedRequestOptions.Single()!.IfMatchEtag);
    }

    [TestMethod]
    public async Task A_lost_ETag_race_is_re_read_and_retried()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-1"), "etag-1");
        container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-1"), "etag-2");
        container.ScriptedUpsertFailures.Enqueue(RecordingCosmosContainerAdapter.PreconditionFailed());
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-2"));

        Assert.IsTrue(applied);
        Assert.AreEqual(2, container.ReadCount);
        Assert.AreEqual("etag-2", container.CapturedRequestOptions.Last()!.IfMatchEtag);
    }

    [TestMethod]
    public async Task Three_lost_races_are_reported_as_transient()
    {
        // A lost compare-and-swap is a transient failure, not a refusal: the Resolver must
        // reschedule and re-evaluate rather than silently drop the outcome.
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        for (var i = 0; i < 3; i++)
        {
            container.EnqueueRead(RowId, "Pending", Row(MessageType.EventRequest, "req-1"), $"etag-{i}");
            container.ScriptedUpsertFailures.Enqueue(RecordingCosmosContainerAdapter.PreconditionFailed());
        }

        var client = new CosmosDbClient(adapter);

        await Assert.ThrowsExactlyAsync<StorageProviderTransientException>(
            () => client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-2")));
        Assert.AreEqual(3, container.ReadCount);
    }

    [TestMethod]
    public async Task An_unparseable_row_status_fails_closed()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueUnparseableRead(RowId, Row(MessageType.EventRequest, "req-1"), "etag-1");
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.EventRequest, "req-2"));

        Assert.IsFalse(applied, "an unreadable row must never be treated as still in flight");
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task An_unguarded_write_still_upserts_without_reading()
    {
        // CLI and test seeds carry MessageType.Unknown; terminal writes are unguarded too.
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadPendingMessage(EventId, SessionId, EndpointId, Row(MessageType.Unknown, "seed-1"));

        Assert.IsTrue(applied);
        Assert.AreEqual(0, container.ReadCount, "the unguarded path must not pay for a point read");
        Assert.AreEqual(1, container.UpsertedItems.Count);
    }

    [TestMethod]
    public async Task A_guarded_deferral_takes_the_same_path()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var container = adapter.Container(EndpointId);
        container.EnqueueRead(RowId, "Completed", Row(MessageType.ResolutionResponse, "rsp-1"), "etag-1", deleted: true);
        var client = new CosmosDbClient(adapter);

        var applied = await client.UploadDeferredMessage(EventId, SessionId, EndpointId, Row(MessageType.DeferralResponse, "def-late"));

        Assert.IsFalse(applied);
        Assert.AreEqual(1, container.ReadCount);
        Assert.AreEqual(0, container.UpsertedItems.Count);
    }

    private static UnresolvedEvent Row(MessageType messageType, string lastMessageId, string? parentMessageId = null) => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        EndpointId = EndpointId,
        EventTypeId = "OrderPlaced",
        MessageType = messageType,
        LastMessageId = lastMessageId,
        ParentMessageId = parentMessageId,
    };
}
