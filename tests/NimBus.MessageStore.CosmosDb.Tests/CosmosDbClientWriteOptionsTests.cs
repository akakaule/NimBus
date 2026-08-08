#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Hot-path Cosmos writes (message tracking upserts, message/audit stores) only
/// ever read the response status code, so they must send
/// <c>EnableContentResponseOnWrite = false</c> — otherwise every write echoes the
/// whole document (EventJson included) back over the wire.
/// </summary>
[TestClass]
public sealed class CosmosDbClientWriteOptionsTests
{
    [TestMethod]
    public async Task UploadPendingMessage_suppresses_content_response_on_write()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        AssertContentSuppressed(adapter.Container("endpoint-1"));
    }

    [TestMethod]
    public async Task UploadCompletedMessage_suppresses_content_response_on_write()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.UploadCompletedMessage("event-1", "session-1", "endpoint-1", NewEvent());

        AssertContentSuppressed(adapter.Container("endpoint-1"));
    }

    [TestMethod]
    public async Task StoreMessage_suppresses_content_response_on_write()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.StoreMessage(new MessageEntity
        {
            MessageId = "message-1",
            EventId = "event-1",
            EndpointId = "endpoint-1",
        });

        AssertContentSuppressed(adapter.Container("messages"));
    }

    [TestMethod]
    public async Task StoreMessageAudit_suppresses_content_response_on_write()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.StoreMessageAudit("event-1", new MessageAuditEntity(), "endpoint-1", "event-type-1");

        AssertContentSuppressed(adapter.Container("audits"));
    }

    private static void AssertContentSuppressed(RecordingCosmosContainerAdapter container)
    {
        Assert.AreEqual(1, container.CapturedRequestOptions.Count, "Expected exactly one upsert.");
        var options = container.CapturedRequestOptions.Single();
        Assert.IsNotNull(options, "Hot-path upserts must pass ItemRequestOptions.");
        Assert.AreEqual(false, options.EnableContentResponseOnWrite,
            "Hot-path upserts must not echo the written document back in the response.");
    }

    private static UnresolvedEvent NewEvent() => new()
    {
        EventId = "event-1",
        EventTypeId = "event-type-1",
    };
}
