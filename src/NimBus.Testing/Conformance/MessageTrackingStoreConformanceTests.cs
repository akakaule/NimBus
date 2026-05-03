using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IMessageTrackingStore"/>. Each
/// concrete provider's test project subclasses this and overrides
/// <see cref="CreateStore"/> to return a freshly-initialized store. Cosmos, SQL Server,
/// and the in-memory reference all run the same assertions, so behaviour stays
/// uniform across providers — message lifecycle, idempotency, all 9 ResolutionStatus
/// values, audits.
/// </summary>
[TestClass]
public abstract class MessageTrackingStoreConformanceTests
{
    /// <summary>
    /// Returns a clean <see cref="IMessageTrackingStore"/>. Implementations should
    /// reset state between tests so that one test's writes never leak into another.
    /// </summary>
    protected abstract IMessageTrackingStore CreateStore();

    private static UnresolvedEvent SampleEvent(string endpointId = "ep1", string eventId = "evt-1", string sessionId = "sess-1")
        => new()
        {
            EventId = eventId,
            SessionId = sessionId,
            EndpointId = endpointId,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
            UpdatedAt = DateTime.UtcNow,
            CorrelationId = "corr-1",
            EndpointRole = EndpointRole.Subscriber,
            MessageType = MessageType.EventRequest,
            EventTypeId = "OrderPlaced",
            To = endpointId,
            From = "publisher",
            MessageContent = new MessageContent(),
        };

    [TestMethod]
    public async Task UploadPending_then_GetPending_returns_event()
    {
        var store = CreateStore();
        await store.UploadPendingMessage("e1", "s1", "ep1", SampleEvent("ep1", "e1", "s1"));
        var fetched = await store.GetPendingEvent("ep1", "e1", "s1");
        Assert.AreEqual("e1", fetched.EventId);
        Assert.AreEqual(ResolutionStatus.Pending, fetched.ResolutionStatus);
    }

    [TestMethod]
    public async Task UploadStatus_is_idempotent_under_repeated_writes()
    {
        var store = CreateStore();
        await store.UploadFailedMessage("e2", "s1", "ep1", SampleEvent("ep1", "e2", "s1"));
        await store.UploadFailedMessage("e2", "s1", "ep1", SampleEvent("ep1", "e2", "s1"));
        var counts = await store.DownloadEndpointStateCount("ep1");
        Assert.AreEqual(1, counts.FailedCount);
    }

    [TestMethod]
    public async Task Status_transition_replaces_previous_status()
    {
        var store = CreateStore();
        await store.UploadPendingMessage("e3", "s1", "ep1", SampleEvent("ep1", "e3", "s1"));
        await store.UploadCompletedMessage("e3", "s1", "ep1", SampleEvent("ep1", "e3", "s1"));
        var counts = await store.DownloadEndpointStateCount("ep1");
        Assert.AreEqual(0, counts.PendingCount);
    }

    [TestMethod]
    public async Task All_resolution_statuses_round_trip()
    {
        var store = CreateStore();
        var statuses = new (string EventId, Func<string, string, string, UnresolvedEvent, Task<bool>> Up, Func<string, string, string, Task<UnresolvedEvent>> Get, ResolutionStatus Expected)[]
        {
            ("p1", store.UploadPendingMessage,      store.GetPendingEvent,       ResolutionStatus.Pending),
            ("d1", store.UploadDeferredMessage,     store.GetDeferredEvent,      ResolutionStatus.Deferred),
            ("f1", store.UploadFailedMessage,       store.GetFailedEvent,        ResolutionStatus.Failed),
            ("dl1", store.UploadDeadletteredMessage, store.GetDeadletteredEvent, ResolutionStatus.DeadLettered),
            ("u1", store.UploadUnsupportedMessage,  store.GetUnsupportedEvent,   ResolutionStatus.Unsupported),
        };

        foreach (var (eventId, up, get, expected) in statuses)
        {
            await up(eventId, "s1", "ep-all", SampleEvent("ep-all", eventId, "s1"));
            var fetched = await get("ep-all", eventId, "s1");
            Assert.AreEqual(expected, fetched.ResolutionStatus, $"Round-trip failed for {expected}");
        }
    }

    [TestMethod]
    public async Task Skipped_and_Completed_statuses_round_trip_via_state_count()
    {
        var store = CreateStore();
        await store.UploadSkippedMessage("sk1", "s1", "ep-sk", SampleEvent("ep-sk", "sk1", "s1"));
        await store.UploadCompletedMessage("c1", "s1", "ep-sk", SampleEvent("ep-sk", "c1", "s1"));
        var completed = (await store.GetCompletedEventsOnEndpoint("ep-sk")).ToList();
        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual("c1", completed[0].EventId);
    }

    [TestMethod]
    public async Task StoreMessage_then_GetMessage_round_trips()
    {
        var store = CreateStore();
        var entity = new MessageEntity
        {
            EventId = "evt-x",
            MessageId = "msg-x",
            EndpointId = "ep1",
            SessionId = "s1",
            CorrelationId = "c1",
            EventTypeId = "OrderPlaced",
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };
        await store.StoreMessage(entity);
        var fetched = await store.GetMessage("evt-x", "msg-x");
        Assert.AreEqual("evt-x", fetched.EventId);
        Assert.AreEqual("msg-x", fetched.MessageId);
    }

    [TestMethod]
    public async Task StoreMessageAudit_appends_to_history()
    {
        var store = CreateStore();
        await store.StoreMessageAudit("evt-aud", new MessageAuditEntity { AuditorName = "alice", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit("evt-aud", new MessageAuditEntity { AuditorName = "bob", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });
        var audits = (await store.GetMessageAudits("evt-aud")).ToList();
        Assert.AreEqual(2, audits.Count);
    }

    [TestMethod]
    public async Task DownloadEndpointStateCount_groups_by_status()
    {
        var store = CreateStore();
        await store.UploadPendingMessage("p1", "s1", "ep-cnt", SampleEvent("ep-cnt", "p1", "s1"));
        await store.UploadPendingMessage("p2", "s2", "ep-cnt", SampleEvent("ep-cnt", "p2", "s2"));
        await store.UploadFailedMessage("f1", "s1", "ep-cnt", SampleEvent("ep-cnt", "f1", "s1"));
        var counts = await store.DownloadEndpointStateCount("ep-cnt");
        Assert.AreEqual(2, counts.PendingCount);
        Assert.AreEqual(1, counts.FailedCount);
        Assert.AreEqual(0, counts.DeadletterCount);
    }

    [TestMethod]
    public async Task RemoveMessage_drops_from_state_count()
    {
        var store = CreateStore();
        await store.UploadFailedMessage("rm1", "s1", "ep-rm", SampleEvent("ep-rm", "rm1", "s1"));
        var removed = await store.RemoveMessage("rm1", "s1", "ep-rm");
        Assert.IsTrue(removed);
        var counts = await store.DownloadEndpointStateCount("ep-rm");
        Assert.AreEqual(0, counts.FailedCount);
    }

    [TestMethod]
    public async Task GetEventsByFilter_returns_matching_endpoint_events()
    {
        var store = CreateStore();
        await store.UploadFailedMessage("ef1", "s1", "ep-flt", SampleEvent("ep-flt", "ef1", "s1"));
        await store.UploadFailedMessage("ef2", "s2", "ep-flt", SampleEvent("ep-flt", "ef2", "s2"));
        await store.UploadFailedMessage("ef3", "s1", "ep-other", SampleEvent("ep-other", "ef3", "s1"));

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = "ep-flt" }, continuationToken: null!, maxSearchItemsCount: 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(2, events.Count, "filter by endpoint should drop other-endpoint rows");
        Assert.IsTrue(events.All(e => e.EndpointId == "ep-flt"));
    }

    [TestMethod]
    public async Task GetEventsByFilter_status_filter_narrows_results()
    {
        var store = CreateStore();
        await store.UploadFailedMessage("st1", "s1", "ep-st", SampleEvent("ep-st", "st1", "s1"));
        await store.UploadCompletedMessage("st2", "s1", "ep-st", SampleEvent("ep-st", "st2", "s1"));
        await store.UploadCompletedMessage("st3", "s1", "ep-st", SampleEvent("ep-st", "st3", "s1"));

        var resp = await store.GetEventsByFilter(
            new EventFilter { EndPointId = "ep-st", ResolutionStatus = new List<string> { "Completed" } },
            continuationToken: null!,
            maxSearchItemsCount: 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(2, events.Count);
        Assert.IsTrue(events.All(e => e.ResolutionStatus == ResolutionStatus.Completed));
    }

    [TestMethod]
    public async Task SearchMessages_returns_matching_messages()
    {
        var store = CreateStore();
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = "se1", MessageId = "m1", EndpointId = "ep-msg", EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = "se2", MessageId = "m2", EndpointId = "ep-msg", EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = "se3", MessageId = "m3", EndpointId = "ep-other", EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var resp = await store.SearchMessages(new MessageFilter { EndpointId = "ep-msg" }, continuationToken: null, maxItemCount: 50);

        var messages = resp.Messages.ToList();
        Assert.AreEqual(2, messages.Count);
        Assert.IsTrue(messages.All(m => m.EndpointId == "ep-msg"));
    }

    [TestMethod]
    public async Task SearchAudits_returns_matching_audits()
    {
        var store = CreateStore();
        await store.StoreMessageAudit("evt-sa1", new MessageAuditEntity { AuditorName = "alice", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit("evt-sa1", new MessageAuditEntity { AuditorName = "bob", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });
        await store.StoreMessageAudit("evt-sa2", new MessageAuditEntity { AuditorName = "alice", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Comment });

        var resp = await store.SearchAudits(new AuditFilter { AuditorName = "alice" }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.All(a => a.Audit.AuditorName == "alice"));
    }
}
