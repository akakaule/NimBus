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
/// Provider-agnostic conformance suite for <see cref="IMessageTrackingStore"/>.
/// Each concrete provider supplies a freshly-initialized store or isolated ids.
/// </summary>
[TestClass]
public abstract class MessageTrackingStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IMessageTrackingStore CreateStore();

    protected string Id(string value) => $"{_scope}-{value}";

    private static string StoredId(string eventId, string sessionId) => $"{eventId}_{sessionId}";

    private static UnresolvedEvent SampleEvent(
        string endpointId,
        string eventId,
        string sessionId,
        EndpointRole endpointRole = EndpointRole.Subscriber) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        EndpointId = endpointId,
        EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
        UpdatedAt = DateTime.UtcNow,
        CorrelationId = "corr-1",
        EndpointRole = endpointRole,
        MessageType = MessageType.EventRequest,
        EventTypeId = "OrderPlaced",
        To = endpointId,
        From = "publisher",
        LastMessageId = "last-message",
        OriginatingMessageId = "origin-message",
        MessageContent = new MessageContent(),
    };

    [TestMethod]
    public async Task UploadPending_then_GetPending_returns_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e1");
        await store.UploadPendingMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var fetched = await store.GetPendingEvent(endpointId, eventId, "s1");

        Assert.AreEqual(eventId, fetched.EventId);
        Assert.AreEqual(ResolutionStatus.Pending, fetched.ResolutionStatus);
    }

    [TestMethod]
    public async Task UploadStatus_is_idempotent_under_repeated_writes()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e2");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(1, counts.FailedCount);
    }

    [TestMethod]
    public async Task Status_transition_replaces_previous_status()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e3");
        await store.UploadPendingMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadCompletedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(0, counts.PendingCount);
    }

    [TestMethod]
    public async Task All_lookup_resolution_statuses_round_trip()
    {
        var store = CreateStore();
        var endpointId = Id("ep-all");
        var statuses = new (string EventId, Func<string, string, string, UnresolvedEvent, Task<bool>> Up, Func<string, string, string, Task<UnresolvedEvent>> Get, ResolutionStatus Expected)[]
        {
            (Id("p1"), store.UploadPendingMessage, store.GetPendingEvent, ResolutionStatus.Pending),
            (Id("d1"), store.UploadDeferredMessage, store.GetDeferredEvent, ResolutionStatus.Deferred),
            (Id("f1"), store.UploadFailedMessage, store.GetFailedEvent, ResolutionStatus.Failed),
            (Id("dl1"), store.UploadDeadletteredMessage, store.GetDeadletteredEvent, ResolutionStatus.DeadLettered),
            (Id("u1"), store.UploadUnsupportedMessage, store.GetUnsupportedEvent, ResolutionStatus.Unsupported),
        };

        foreach (var (eventId, up, get, expected) in statuses)
        {
            await up(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
            var fetched = await get(endpointId, eventId, "s1");
            Assert.AreEqual(expected, fetched.ResolutionStatus, $"Round-trip failed for {expected}");
        }
    }

    [TestMethod]
    public async Task Skipped_and_Completed_statuses_round_trip_via_completed_listing()
    {
        var store = CreateStore();
        var endpointId = Id("ep-sk");
        var skippedId = Id("sk1");
        var completedId = Id("c1");
        await store.UploadSkippedMessage(skippedId, "s1", endpointId, SampleEvent(endpointId, skippedId, "s1"));
        await store.UploadCompletedMessage(completedId, "s1", endpointId, SampleEvent(endpointId, completedId, "s1"));

        var completed = (await store.GetCompletedEventsOnEndpoint(endpointId)).ToList();

        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual(completedId, completed[0].EventId);
    }

    [TestMethod]
    public async Task StoreMessage_then_GetMessage_round_trips()
    {
        var store = CreateStore();
        var eventId = Id("evt-x");
        var messageId = Id("msg-x");
        var entity = new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = Id("ep1"),
            SessionId = "s1",
            CorrelationId = "c1",
            EventTypeId = "OrderPlaced",
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };
        await store.StoreMessage(entity);

        var fetched = await store.GetMessage(eventId, messageId);

        Assert.AreEqual(eventId, fetched.EventId);
        Assert.AreEqual(messageId, fetched.MessageId);
    }

    [TestMethod]
    public async Task StoreMessageAudit_appends_to_history()
    {
        var store = CreateStore();
        var eventId = Id("evt-aud");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(2, audits.Count);
    }

    [TestMethod]
    public async Task DownloadEndpointStateCount_groups_by_status()
    {
        var store = CreateStore();
        var endpointId = Id("ep-cnt");
        await store.UploadPendingMessage(Id("p1"), "s1", endpointId, SampleEvent(endpointId, Id("p1"), "s1"));
        await store.UploadPendingMessage(Id("p2"), "s2", endpointId, SampleEvent(endpointId, Id("p2"), "s2"));
        await store.UploadFailedMessage(Id("f1"), "s1", endpointId, SampleEvent(endpointId, Id("f1"), "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(2, counts.PendingCount);
        Assert.AreEqual(1, counts.FailedCount);
        Assert.AreEqual(0, counts.DeadletterCount);
    }

    [TestMethod]
    public async Task DownloadEndpointSessionStateCount_returns_pending_and_deferred_event_ids()
    {
        var store = CreateStore();
        var endpointId = Id("ep-session");
        var pendingId = Id("sess-pending");
        var deferredId = Id("sess-deferred");
        var failedId = Id("sess-failed");
        await store.UploadPendingMessage(pendingId, "session-1", endpointId, SampleEvent(endpointId, pendingId, "session-1"));
        await store.UploadDeferredMessage(deferredId, "session-1", endpointId, SampleEvent(endpointId, deferredId, "session-1"));
        await store.UploadFailedMessage(failedId, "session-1", endpointId, SampleEvent(endpointId, failedId, "session-1"));

        var session = await store.DownloadEndpointSessionStateCount(endpointId, "session-1");

        CollectionAssert.Contains(session.PendingEvents.ToList(), StoredId(pendingId, "session-1"));
        CollectionAssert.Contains(session.DeferredEvents.ToList(), StoredId(deferredId, "session-1"));
        CollectionAssert.DoesNotContain(session.PendingEvents.Concat(session.DeferredEvents).ToList(), StoredId(failedId, "session-1"));
    }

    [TestMethod]
    public async Task DownloadEndpointSessionStateCountBatch_groups_sessions()
    {
        var store = CreateStore();
        var endpointId = Id("ep-session-batch");
        var sessionOneId = Id("batch-pending");
        var sessionTwoId = Id("batch-deferred");
        await store.UploadPendingMessage(sessionOneId, "session-1", endpointId, SampleEvent(endpointId, sessionOneId, "session-1"));
        await store.UploadDeferredMessage(sessionTwoId, "session-2", endpointId, SampleEvent(endpointId, sessionTwoId, "session-2"));

        var sessions = (await store.DownloadEndpointSessionStateCountBatch(endpointId, new[] { "session-1", "session-2" })).ToList();

        var sessionOne = sessions.Single(s => s.SessionId == "session-1");
        var sessionTwo = sessions.Single(s => s.SessionId == "session-2");
        CollectionAssert.Contains(sessionOne.PendingEvents.ToList(), StoredId(sessionOneId, "session-1"));
        CollectionAssert.Contains(sessionTwo.DeferredEvents.ToList(), StoredId(sessionTwoId, "session-2"));
    }

    [TestMethod]
    public async Task DownloadEndpointStatePaging_lists_actionable_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-state");
        var pendingId = Id("state-pending");
        var deferredId = Id("state-deferred");
        var failedId = Id("state-failed");
        var deadletteredId = Id("state-dlq");
        var unsupportedId = Id("state-unsupported");
        var completedId = Id("state-completed");
        await store.UploadPendingMessage(pendingId, "s1", endpointId, SampleEvent(endpointId, pendingId, "s1"));
        await store.UploadDeferredMessage(deferredId, "s1", endpointId, SampleEvent(endpointId, deferredId, "s1"));
        await store.UploadFailedMessage(failedId, "s1", endpointId, SampleEvent(endpointId, failedId, "s1"));
        await store.UploadDeadletteredMessage(deadletteredId, "s1", endpointId, SampleEvent(endpointId, deadletteredId, "s1"));
        await store.UploadUnsupportedMessage(unsupportedId, "s1", endpointId, SampleEvent(endpointId, unsupportedId, "s1"));
        await store.UploadCompletedMessage(completedId, "s1", endpointId, SampleEvent(endpointId, completedId, "s1"));

        var state = await store.DownloadEndpointStatePaging(endpointId, pageSize: 20, continuationToken: string.Empty);

        CollectionAssert.Contains(state.PendingEvents.ToList(), StoredId(pendingId, "s1"));
        CollectionAssert.Contains(state.DeferredEvents.ToList(), StoredId(deferredId, "s1"));
        CollectionAssert.Contains(state.FailedEvents.ToList(), StoredId(failedId, "s1"));
        CollectionAssert.Contains(state.DeadletteredEvents.ToList(), StoredId(deadletteredId, "s1"));
        CollectionAssert.Contains(state.UnsupportedEvents.ToList(), StoredId(unsupportedId, "s1"));
        CollectionAssert.Contains(state.GetAllUnresolvedEvents.ToList(), StoredId(deadletteredId, "s1"));
        CollectionAssert.Contains(state.GetAllUnresolvedEvents.ToList(), StoredId(unsupportedId, "s1"));
        CollectionAssert.DoesNotContain(state.GetAllUnresolvedEvents.ToList(), StoredId(completedId, "s1"));
        Assert.AreEqual(5, state.EnrichedUnresolvedEvents.Count());
    }

    [TestMethod]
    public async Task GetEventsByIds_accepts_ids_returned_by_endpoint_state()
    {
        var store = CreateStore();
        var endpointId = Id("ep-state-ids");
        var pendingId = Id("ids-pending");
        var failedId = Id("ids-failed");
        await store.UploadPendingMessage(pendingId, "s1", endpointId, SampleEvent(endpointId, pendingId, "s1"));
        await store.UploadFailedMessage(failedId, "s2", endpointId, SampleEvent(endpointId, failedId, "s2"));

        var state = await store.DownloadEndpointStatePaging(endpointId, pageSize: 20, continuationToken: string.Empty);
        var events = await store.GetEventsByIds(endpointId, state.GetAllUnresolvedEvents);

        var eventIds = events.Select(e => e.EventId).ToList();
        CollectionAssert.Contains(eventIds, pendingId);
        CollectionAssert.Contains(eventIds, failedId);
    }

    [TestMethod]
    public async Task GetEndpointErrorList_returns_failed_and_deferred_event_ids()
    {
        var store = CreateStore();
        var endpointId = Id("ep-error-list");
        var failedId = Id("error-failed");
        var deferredId = Id("error-deferred");
        var pendingId = Id("error-pending");
        await store.UploadFailedMessage(failedId, "s1", endpointId, SampleEvent(endpointId, failedId, "s1"));
        await store.UploadDeferredMessage(deferredId, "s2", endpointId, SampleEvent(endpointId, deferredId, "s2"));
        await store.UploadPendingMessage(pendingId, "s3", endpointId, SampleEvent(endpointId, pendingId, "s3"));

        var errorList = await store.GetEndpointErrorList(endpointId);

        StringAssert.Contains(errorList, StoredId(failedId, "s1"));
        StringAssert.Contains(errorList, StoredId(deferredId, "s2"));
        Assert.IsFalse(errorList.Contains(StoredId(pendingId, "s3"), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_returns_pending_and_deferred_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked");
        var pendingId = Id("blocked-pending");
        var deferredId = Id("blocked-deferred");
        var failedId = Id("blocked-failed");
        await store.UploadPendingMessage(pendingId, "session-1", endpointId, SampleEvent(endpointId, pendingId, "session-1"));
        await store.UploadDeferredMessage(deferredId, "session-1", endpointId, SampleEvent(endpointId, deferredId, "session-1"));
        await store.UploadFailedMessage(failedId, "session-1", endpointId, SampleEvent(endpointId, failedId, "session-1"));

        var page = await store.GetBlockedEventsOnSession(endpointId, "session-1", 0, 100);

        Assert.AreEqual(2, page.Total);
        Assert.AreEqual(2, page.Items.Count);
        Assert.IsTrue(page.Items.Any(e => e.EventId == pendingId && e.Status == ResolutionStatus.Pending.ToString()));
        Assert.IsTrue(page.Items.Any(e => e.EventId == deferredId && e.Status == ResolutionStatus.Deferred.ToString()));
        Assert.IsFalse(page.Items.Any(e => e.EventId == failedId));
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_pages_results_and_reports_total()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked-paged");

        // Seed 5 blocked siblings on the same session (3 pending + 2 deferred).
        for (var i = 0; i < 3; i++)
        {
            var id = Id($"paged-pending-{i}");
            await store.UploadPendingMessage(id, "session-paged", endpointId, SampleEvent(endpointId, id, "session-paged"));
        }
        for (var i = 0; i < 2; i++)
        {
            var id = Id($"paged-deferred-{i}");
            await store.UploadDeferredMessage(id, "session-paged", endpointId, SampleEvent(endpointId, id, "session-paged"));
        }

        var firstPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 0, 2);
        Assert.AreEqual(5, firstPage.Total);
        Assert.AreEqual(2, firstPage.Items.Count);

        var secondPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 2, 2);
        Assert.AreEqual(5, secondPage.Total);
        Assert.AreEqual(2, secondPage.Items.Count);

        var thirdPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 4, 2);
        Assert.AreEqual(5, thirdPage.Total);
        Assert.AreEqual(1, thirdPage.Items.Count);

        // No overlap across pages.
        var combined = firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items)
            .Select(e => e.EventId).Distinct().Count();
        Assert.AreEqual(5, combined);
    }

    [TestMethod]
    public async Task GetInvalidEventsOnSession_returns_publisher_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-invalid");
        var publisherId = Id("invalid-publisher");
        var subscriberId = Id("invalid-subscriber");
        await store.UploadPendingMessage(publisherId, "session-1", endpointId, SampleEvent(endpointId, publisherId, "session-1", EndpointRole.Publisher));
        await store.UploadPendingMessage(subscriberId, "session-1", endpointId, SampleEvent(endpointId, subscriberId, "session-1", EndpointRole.Subscriber));

        var invalid = (await store.GetInvalidEventsOnSession(endpointId)).ToList();

        Assert.AreEqual(1, invalid.Count);
        Assert.AreEqual(publisherId, invalid[0].EventId);
    }

    [TestMethod]
    public async Task RemoveMessage_drops_from_state_count()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rm");
        var eventId = Id("rm1");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var removed = await store.RemoveMessage(eventId, "s1", endpointId);
        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, counts.FailedCount);
    }

    [TestMethod]
    public async Task GetEventsByFilter_returns_matching_endpoint_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-flt");
        var otherEndpointId = Id("ep-other");
        await store.UploadFailedMessage(Id("ef1"), "s1", endpointId, SampleEvent(endpointId, Id("ef1"), "s1"));
        await store.UploadFailedMessage(Id("ef2"), "s2", endpointId, SampleEvent(endpointId, Id("ef2"), "s2"));
        await store.UploadFailedMessage(Id("ef3"), "s1", otherEndpointId, SampleEvent(otherEndpointId, Id("ef3"), "s1"));

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId }, continuationToken: null!, maxSearchItemsCount: 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(2, events.Count, "filter by endpoint should drop other-endpoint rows");
        Assert.IsTrue(events.All(e => e.EndpointId == endpointId));
    }

    [TestMethod]
    public async Task GetEventsByFilter_status_filter_narrows_results()
    {
        var store = CreateStore();
        var endpointId = Id("ep-st");
        await store.UploadFailedMessage(Id("st1"), "s1", endpointId, SampleEvent(endpointId, Id("st1"), "s1"));
        await store.UploadCompletedMessage(Id("st2"), "s1", endpointId, SampleEvent(endpointId, Id("st2"), "s1"));
        await store.UploadCompletedMessage(Id("st3"), "s1", endpointId, SampleEvent(endpointId, Id("st3"), "s1"));

        var resp = await store.GetEventsByFilter(
            new EventFilter { EndPointId = endpointId, ResolutionStatus = new List<string> { "Completed" } },
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
        var endpointId = Id("ep-msg");
        var otherEndpointId = Id("ep-other");
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = Id("se1"), MessageId = Id("m1"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("se2"), MessageId = Id("m2"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("se3"), MessageId = Id("m3"), EndpointId = otherEndpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var resp = await store.SearchMessages(new MessageFilter { EndpointId = endpointId }, continuationToken: null, maxItemCount: 50);

        var messages = resp.Messages.ToList();
        Assert.AreEqual(2, messages.Count);
        Assert.IsTrue(messages.All(m => m.EndpointId == endpointId));
    }

    [TestMethod]
    public async Task PendingHandoff_fields_round_trip()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff");
        var eventId = Id("handoff-1");
        var sessionId = "session-handoff";
        var expectedBy = new DateTime(2026, 06, 01, 09, 00, 00, DateTimeKind.Utc);

        var sample = SampleEvent(endpointId, eventId, sessionId);
        sample.MessageType = MessageType.PendingHandoffResponse;
        sample.PendingSubStatus = "Handoff";
        sample.HandoffReason = "DMF import in progress";
        sample.ExternalJobId = "DMF-JOB-42";
        sample.ExpectedBy = expectedBy;

        await store.UploadPendingMessage(eventId, sessionId, endpointId, sample);

        var fetched = await store.GetPendingEvent(endpointId, eventId, sessionId);

        Assert.AreEqual("Handoff", fetched.PendingSubStatus);
        Assert.AreEqual("DMF import in progress", fetched.HandoffReason);
        Assert.AreEqual("DMF-JOB-42", fetched.ExternalJobId);
        Assert.IsNotNull(fetched.ExpectedBy);
        Assert.AreEqual(expectedBy, fetched.ExpectedBy.Value.ToUniversalTime());
    }

    [TestMethod]
    public async Task SearchAudits_returns_matching_audits()
    {
        var store = CreateStore();
        var auditor = Id("alice");
        await store.StoreMessageAudit(Id("evt-sa1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(Id("evt-sa1"), new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });
        await store.StoreMessageAudit(Id("evt-sa2"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Comment });

        var resp = await store.SearchAudits(new AuditFilter { AuditorName = auditor }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.All(a => a.Audit.AuditorName == auditor));
    }
}
