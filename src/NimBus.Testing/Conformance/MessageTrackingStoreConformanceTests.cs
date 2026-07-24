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
    public async Task GetPendingHandoffByExternalJobId_returns_the_pending_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff");
        var eventId = Id("e-handoff");
        var externalJobId = Id("ext-job");

        var content = SampleEvent(endpointId, eventId, "s-handoff");
        content.PendingSubStatus = "Handoff";
        content.HandoffReason = "Awaiting external work";
        content.ExternalJobId = externalJobId;
        await store.UploadPendingMessage(eventId, "s-handoff", endpointId, content);

        var fetched = await store.GetPendingHandoffByExternalJobId(endpointId, externalJobId);

        Assert.IsNotNull(fetched);
        Assert.AreEqual(eventId, fetched.EventId);
        Assert.AreEqual("Handoff", fetched.PendingSubStatus);
        Assert.AreEqual(externalJobId, fetched.ExternalJobId);
    }

    [TestMethod]
    public async Task GetPendingHandoffByExternalJobId_returns_null_when_no_match()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff-miss");

        var fetched = await store.GetPendingHandoffByExternalJobId(endpointId, Id("never-registered"));

        Assert.IsNull(fetched);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_returns_only_the_handoff_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next");

        // A plain pending event (no sub-status) and a failed event must be ignored;
        // only the single Pending+Handoff row should come back.
        var plain = SampleEvent(endpointId, Id("e-plain"), "s1");
        await store.UploadPendingMessage(plain.EventId, "s1", endpointId, plain);

        var failed = SampleEvent(endpointId, Id("e-failed"), "s2");
        failed.PendingSubStatus = "Handoff";
        await store.UploadFailedMessage(failed.EventId, "s2", endpointId, failed);

        var handoff = SampleEvent(endpointId, Id("e-handoff"), "s3");
        handoff.PendingSubStatus = "Handoff";
        handoff.ExternalJobId = Id("job");
        await store.UploadPendingMessage(handoff.EventId, "s3", endpointId, handoff);

        var fetched = await store.GetNextPendingHandoffEvent(endpointId, null);

        Assert.IsNotNull(fetched);
        Assert.AreEqual(handoff.EventId, fetched.EventId);
        Assert.AreEqual("Handoff", fetched.PendingSubStatus);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_returns_null_when_no_handoff()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next-miss");

        var plain = SampleEvent(endpointId, Id("e-plain"), "s1");
        await store.UploadPendingMessage(plain.EventId, "s1", endpointId, plain);

        var fetched = await store.GetNextPendingHandoffEvent(endpointId, null);

        Assert.IsNull(fetched);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_respects_eventTypeIds_filter()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next-filter");

        var handoff = SampleEvent(endpointId, Id("e-handoff"), "s1");
        handoff.PendingSubStatus = "Handoff";
        handoff.EventTypeId = "OrderPlaced";
        await store.UploadPendingMessage(handoff.EventId, "s1", endpointId, handoff);

        // A non-matching type filter finds nothing.
        Assert.IsNull(await store.GetNextPendingHandoffEvent(endpointId, new[] { "SomethingElse" }));

        // A matching type filter finds the row.
        var matched = await store.GetNextPendingHandoffEvent(endpointId, new[] { "OrderPlaced", "AnotherType" });
        Assert.IsNotNull(matched);
        Assert.AreEqual(handoff.EventId, matched.EventId);
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
        var skippedEvent = SampleEvent(endpointId, skippedId, "s1");
        skippedEvent.MessageContent.ErrorContent = new ErrorContent
        {
            ErrorText = "InvalidOperationException: discarded by PartnerFailureDispositionClassifier",
            ErrorType = nameof(InvalidOperationException),
        };
        await store.UploadSkippedMessage(skippedId, "s1", endpointId, skippedEvent);
        await store.UploadCompletedMessage(completedId, "s1", endpointId, SampleEvent(endpointId, completedId, "s1"));

        var completed = (await store.GetCompletedEventsOnEndpoint(endpointId)).ToList();
        var skipped = await store.GetEvent(endpointId, skippedId);

        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual(completedId, completed[0].EventId);
        Assert.AreEqual(ResolutionStatus.Skipped, skipped.ResolutionStatus);
        Assert.AreEqual(nameof(InvalidOperationException), skipped.MessageContent.ErrorContent.ErrorType);
        StringAssert.Contains(skipped.MessageContent.ErrorContent.ErrorText, "PartnerFailureDispositionClassifier");
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
            CloudEventId = "ce-message-1",
            CloudEventSource = "urn:nimbus:conformance",
            CloudEventType = "com.nimbus.order-placed.v1",
            CloudEventSubject = "orders/42",
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };
        await store.StoreMessage(entity);

        var fetched = await store.GetMessage(eventId, messageId);

        Assert.AreEqual(eventId, fetched.EventId);
        Assert.AreEqual(messageId, fetched.MessageId);
        Assert.AreEqual("ce-message-1", fetched.CloudEventId);
        Assert.AreEqual("urn:nimbus:conformance", fetched.CloudEventSource);
        Assert.AreEqual("com.nimbus.order-placed.v1", fetched.CloudEventType);
        Assert.AreEqual("orders/42", fetched.CloudEventSubject);
    }

    [TestMethod]
    public async Task CloudEvent_identity_round_trips_on_tracked_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep-ce");
        var eventId = Id("event-ce");
        var stored = SampleEvent(endpointId, eventId, "session-ce");
        stored.CloudEventId = "ce-event-1";
        stored.CloudEventSource = "urn:nimbus:conformance";
        stored.CloudEventType = "com.nimbus.order-placed.v1";
        stored.CloudEventSubject = "orders/42";

        await store.UploadFailedMessage(eventId, "session-ce", endpointId, stored);

        var fetched = await store.GetFailedEvent(endpointId, eventId, "session-ce");
        Assert.AreEqual("ce-event-1", fetched.CloudEventId);
        Assert.AreEqual("urn:nimbus:conformance", fetched.CloudEventSource);
        Assert.AreEqual("com.nimbus.order-placed.v1", fetched.CloudEventType);
        Assert.AreEqual("orders/42", fetched.CloudEventSubject);
    }

    [TestMethod]
    public async Task Native_message_and_event_keep_CloudEvent_identity_null()
    {
        var store = CreateStore();
        var endpointId = Id("ep-native");
        var eventId = Id("event-native");
        var messageId = Id("message-native");

        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        });
        await store.UploadPendingMessage(eventId, "session-native", endpointId,
            SampleEvent(endpointId, eventId, "session-native"));

        var message = await store.GetMessage(eventId, messageId);
        var trackedEvent = await store.GetPendingEvent(endpointId, eventId, "session-native");

        Assert.IsNull(message.CloudEventId);
        Assert.IsNull(message.CloudEventSource);
        Assert.IsNull(message.CloudEventType);
        Assert.IsNull(message.CloudEventSubject);
        Assert.IsNull(trackedEvent.CloudEventId);
        Assert.IsNull(trackedEvent.CloudEventSource);
        Assert.IsNull(trackedEvent.CloudEventType);
        Assert.IsNull(trackedEvent.CloudEventSubject);
    }

    [TestMethod]
    public async Task GetLatestEventRequestMessage_returns_newest_request_with_payload()
    {
        var store = CreateStore();
        var eventId = Id("evt-lr");
        var ep = Id("ep1");
        var now = DateTime.UtcNow;

        // Older EventRequest carrying a payload.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m1"), EndpointId = ep,
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = now.AddMinutes(-10),
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":1}" } },
        });
        // Newer ResubmissionRequest carrying a payload — this is the one that should win.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m2"), EndpointId = ep,
            MessageType = MessageType.ResubmissionRequest,
            EnqueuedTimeUtc = now.AddMinutes(-2),
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":2}" } },
        });
        // Newest message overall, but not a request type — must be ignored.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m3"), EndpointId = ep,
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = now,
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":3}" } },
        });

        var latest = await store.GetLatestEventRequestMessage(eventId);

        Assert.IsNotNull(latest);
        Assert.AreEqual("{\"v\":2}", latest.MessageContent?.EventContent?.EventJson);
    }

    [TestMethod]
    public async Task GetLatestEventRequestMessage_returns_null_when_no_request_carries_payload()
    {
        var store = CreateStore();
        var eventId = Id("evt-lr-none");
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m1"), EndpointId = Id("ep1"),
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        });

        var latest = await store.GetLatestEventRequestMessage(eventId);

        Assert.IsNull(latest);
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
    public async Task PurgeMessages_removes_only_the_target_session()
    {
        var store = CreateStore();
        var endpointId = Id("ep-purge");
        var purgedPendingId = Id("purge-pending");
        var purgedDeferredId = Id("purge-deferred");
        var keptId = Id("purge-kept");
        await store.UploadPendingMessage(purgedPendingId, "session-purged", endpointId, SampleEvent(endpointId, purgedPendingId, "session-purged"));
        await store.UploadDeferredMessage(purgedDeferredId, "session-purged", endpointId, SampleEvent(endpointId, purgedDeferredId, "session-purged"));
        await store.UploadPendingMessage(keptId, "session-kept", endpointId, SampleEvent(endpointId, keptId, "session-kept"));

        var purged = await store.PurgeMessages(endpointId, "session-purged");

        Assert.IsTrue(purged);
        var purgedPage = await store.GetBlockedEventsOnSession(endpointId, "session-purged", 0, 100);
        Assert.AreEqual(0, purgedPage.Total);
        Assert.AreEqual(0, purgedPage.Items.Count);
        var keptPage = await store.GetBlockedEventsOnSession(endpointId, "session-kept", 0, 100);
        Assert.AreEqual(1, keptPage.Total);
        Assert.IsTrue(keptPage.Items.Any(e => e.EventId == keptId));
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
        // Tick comparison, not ToUniversalTime(): SQL Server returns datetime2 as
        // Kind=Unspecified, and ToUniversalTime() on Unspecified applies the local
        // machine's offset — the assertion would only pass on UTC machines.
        Assert.AreEqual(expectedBy, fetched.ExpectedBy.Value);
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

    // ───── Spec 008: round-trip of the four new MessageAuditEntity fields ─────

    [TestMethod]
    public async Task StoreMessageAudit_roundtrips_AccessDenied_and_Data_fields()
    {
        var store = CreateStore();
        var eventId = Id("evt-aud-new");
        var endpointId = Id("ep-aud-new");
        var dataPayload = "{\"filter\":\"OrderPlaced\",\"endpointId\":\"" + endpointId + "\"}";

        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = Id("alice"),
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.SearchEvents,
            AccessDenied = true,
            Data = dataPayload,
            EventId = eventId,
            EndpointId = endpointId,
            CloudEventId = "ce-audit-1",
            CloudEventSource = "urn:nimbus:audit",
            CloudEventType = "com.nimbus.audit.v1",
            CloudEventSubject = "audits/42",
        }, endpointId, eventTypeId: "OrderPlaced");

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(1, audits.Count);
        var audit = audits[0];
        Assert.IsTrue(audit.AccessDenied, "AccessDenied should round-trip as true");
        Assert.AreEqual(dataPayload, audit.Data, "Data payload should round-trip");
        // Mirrored EventId/EndpointId are populated by the writer where supported
        // (SQL: read from row columns; Cosmos: from the serialized document; in-memory: from the entity itself).
        Assert.AreEqual(eventId, audit.EventId);
        Assert.AreEqual(endpointId, audit.EndpointId);
        Assert.AreEqual("ce-audit-1", audit.CloudEventId);
        Assert.AreEqual("urn:nimbus:audit", audit.CloudEventSource);
        Assert.AreEqual("com.nimbus.audit.v1", audit.CloudEventType);
        Assert.AreEqual("audits/42", audit.CloudEventSubject);
    }

    [TestMethod]
    public async Task StoreMessageAudit_legacy_row_defaults_AccessDenied_false_and_Data_null()
    {
        // Per spec FR-074: rows written without the new fields (the legacy code
        // path, or a row created from a pre-migration entity) MUST project with
        // AccessDenied = false and Data = null so the audit-list UI renders unchanged.
        var store = CreateStore();
        var eventId = Id("evt-aud-legacy");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = Id("legacy-user"),
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.Resubmit,
        });

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(1, audits.Count);
        Assert.IsFalse(audits[0].AccessDenied, "legacy row should default AccessDenied to false");
        Assert.IsNull(audits[0].Data, "legacy row should default Data to null");
        Assert.IsNull(audits[0].CloudEventId, "legacy native-message audit should not gain a CloudEvent id");
        Assert.IsNull(audits[0].CloudEventSource);
        Assert.IsNull(audits[0].CloudEventType);
        Assert.IsNull(audits[0].CloudEventSubject);
    }

    [TestMethod]
    public async Task SearchAudits_projects_AccessDenied_and_Data_fields()
    {
        var store = CreateStore();
        var auditor = Id("eve");
        var eventId = Id("evt-sa-new");
        var endpointId = Id("ep-sa-new");

        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = auditor,
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.GetEventDetails,
            AccessDenied = true,
            Data = "context-payload",
            EventId = eventId,
            EndpointId = endpointId,
            CloudEventId = "ce-audit-search-1",
            CloudEventSource = "urn:nimbus:audit-search",
            CloudEventType = "com.nimbus.audit-search.v1",
            CloudEventSubject = "audits/search/42",
        }, endpointId);

        var resp = await store.SearchAudits(new AuditFilter { AuditorName = auditor }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(1, items.Count);
        Assert.IsTrue(items[0].Audit.AccessDenied);
        Assert.AreEqual("context-payload", items[0].Audit.Data);
        Assert.AreEqual("ce-audit-search-1", items[0].Audit.CloudEventId);
        Assert.AreEqual("urn:nimbus:audit-search", items[0].Audit.CloudEventSource);
        Assert.AreEqual("com.nimbus.audit-search.v1", items[0].Audit.CloudEventType);
        Assert.AreEqual("audits/search/42", items[0].Audit.CloudEventSubject);
    }

    [TestMethod]
    public async Task GetResubmitCounts_counts_resubmit_audits_per_event_excluding_denied()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rc");
        var eventA = Id("evt-rc-a");
        var eventB = Id("evt-rc-b");

        // eventA: two granted resubmits (one plain, one with changes), one denied
        // attempt (must not count), one unrelated audit type (must not count).
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.ResubmitWithChanges, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("mallory"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, AccessDenied = true, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip, EventId = eventA, EndpointId = endpointId }, endpointId);
        // eventB: no resubmit audits at all — must be absent from the result.
        await store.StoreMessageAudit(eventB, new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Comment, EventId = eventB, EndpointId = endpointId }, endpointId);

        var counts = await store.GetResubmitCounts(endpointId, new[] { eventA, eventB });

        Assert.AreEqual(2, counts.GetValueOrDefault(eventA), "granted Resubmit + ResubmitWithChanges count; denied and unrelated audits do not");
        Assert.IsFalse(counts.ContainsKey(eventB), "events without resubmit audits are absent (missing = 0)");
    }

    [TestMethod]
    public async Task GetResubmitCounts_returns_empty_for_empty_input_or_foreign_endpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rc2");
        var eventId = Id("evt-rc2");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = eventId, EndpointId = endpointId }, endpointId);

        var emptyIds = await store.GetResubmitCounts(endpointId, Array.Empty<string>());
        Assert.AreEqual(0, emptyIds.Count);

        // Audits are endpoint-scoped: the same event id queried under another
        // endpoint must not leak counts across endpoints.
        var foreign = await store.GetResubmitCounts(Id("ep-other"), new[] { eventId });
        Assert.AreEqual(0, foreign.Count);
    }

    [TestMethod]
    public async Task SearchAudits_scopes_by_endpointId()
    {
        var store = CreateStore();
        var endpointId = Id("ep-audit-scope");
        var auditor = Id("carol");
        await store.StoreMessageAudit(Id("evt-as1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = Id("evt-as1"), EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(Id("evt-as2"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip, EventId = Id("evt-as2"), EndpointId = Id("ep-audit-other") }, Id("ep-audit-other"));

        var resp = await store.SearchAudits(new AuditFilter { EndpointId = endpointId }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(1, items.Count, "only the requested endpoint's audits are returned");
        Assert.AreEqual(Id("evt-as1"), items[0].EventId);
        Assert.AreEqual(endpointId, items[0].EndpointId, "EndpointId is projected so callers can build routes");
    }

    [TestMethod]
    public async Task SetEventReport_roundtrips_and_updates_in_place()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep");
        var eventId = Id("evt-rep");

        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("alice"), ticketId: "INC0042");

        var reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.IsTrue(reports.TryGetValue(eventId, out var report));
        Assert.IsTrue(report!.IsReported);
        Assert.AreEqual(Id("alice"), report.ReportedBy);
        Assert.AreEqual("INC0042", report.TicketId);
        Assert.IsNotNull(report.ReportedAtUtc);

        // Upsert: a second toggle for the same (endpoint, event) replaces the
        // marker instead of adding a row.
        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("bob"), ticketId: "JIRA-7");
        reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(Id("bob"), reports[eventId].ReportedBy);
        Assert.AreEqual("JIRA-7", reports[eventId].TicketId);
    }

    [TestMethod]
    public async Task SetEventReport_clearing_drops_the_ticket_reference()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep2");
        var eventId = Id("evt-rep2");

        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("alice"), ticketId: "INC0042");
        await store.SetEventReport(endpointId, eventId, isReported: false, reportedBy: Id("alice"), ticketId: "INC0042");

        var reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.IsTrue(reports.TryGetValue(eventId, out var report));
        Assert.IsFalse(report!.IsReported);
        Assert.IsNull(report.TicketId, "clearing the marker must drop the ticket reference");
    }

    [TestMethod]
    public async Task EventReports_do_not_collide_on_ambiguous_composite_keys()
    {
        // ("a_b", "c") and ("a", "b_c") concatenate to the same string — the
        // store key must be the (endpointId, eventId) PAIR, not a joined string.
        var store = CreateStore();
        var prefix = Id("ep-amb");
        await store.SetEventReport($"{prefix}_x", "y", isReported: true, reportedBy: Id("alice"), ticketId: "T-1");

        var other = await store.GetEventReports(prefix, new[] { "x_y" });
        Assert.AreEqual(0, other.Count, "a report on endpoint '{prefix}_x' must not surface for endpoint '{prefix}'");

        var own = await store.GetEventReports($"{prefix}_x", new[] { "y" });
        Assert.AreEqual(1, own.Count);
    }

    [TestMethod]
    public async Task GetEventReports_batches_and_scopes_by_endpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep3");
        var reported = Id("evt-rep3-a");
        var unreported = Id("evt-rep3-b");
        await store.SetEventReport(endpointId, reported, isReported: true, reportedBy: Id("alice"), ticketId: null);

        var reports = await store.GetEventReports(endpointId, new[] { reported, unreported });
        Assert.AreEqual(1, reports.Count, "events never reported are absent (missing = not reported)");
        Assert.IsTrue(reports.ContainsKey(reported));
        Assert.IsNull(reports[reported].TicketId, "reporting without a ticket keeps TicketId null");

        var empty = await store.GetEventReports(endpointId, Array.Empty<string>());
        Assert.AreEqual(0, empty.Count);

        // Markers are endpoint-scoped: the same event id under another endpoint
        // must not leak.
        var foreign = await store.GetEventReports(Id("ep-rep3-other"), new[] { reported });
        Assert.AreEqual(0, foreign.Count);
    }

    // ───── Prefix-search semantics (cross-provider contract) ─────
    // ID-like filter fields match by case-insensitive PREFIX on every provider:
    // Cosmos STARTSWITH(x, y, true), SQL Server LIKE 'y%' (CI collation),
    // in-memory StartsWith(OrdinalIgnoreCase). Mid-string fragments do NOT match.

    [TestMethod]
    public async Task SearchMessages_matches_eventId_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var endpointId = Id("ep-prefix");
        var eventId = Id("prefix-event-abc");
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = eventId, MessageId = Id("pm1"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("other-event"), MessageId = Id("pm2"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var prefix = eventId[..(_scope.Length + 8)];
        var byPrefix = await store.SearchMessages(new MessageFilter { EventId = prefix }, null, 50);
        Assert.AreEqual(1, byPrefix.Messages.Count(), "A leading fragment of the event id must match (prefix semantics).");
        Assert.AreEqual(eventId, byPrefix.Messages.Single().EventId);

        var byUppercasePrefix = await store.SearchMessages(new MessageFilter { EventId = prefix.ToUpperInvariant() }, null, 50);
        Assert.AreEqual(1, byUppercasePrefix.Messages.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = eventId[4..12];
        var byMidFragment = await store.SearchMessages(new MessageFilter { EventId = midFragment }, null, 50);
        Assert.AreEqual(0, byMidFragment.Messages.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    [TestMethod]
    public async Task GetEventsByFilter_matches_eventId_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var endpointId = Id("ep-evprefix");
        var eventId = Id("prefix-ev-abc");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadFailedMessage(Id("other-ev"), "s1", endpointId, SampleEvent(endpointId, Id("other-ev"), "s1"));

        var prefix = eventId[..(_scope.Length + 8)];
        var byPrefix = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = prefix }, null!, 50);
        Assert.AreEqual(1, byPrefix.Events.Count(), "A leading fragment of the event id must match (prefix semantics).");
        Assert.AreEqual(eventId, byPrefix.Events.Single().EventId);

        var byUppercasePrefix = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = prefix.ToUpperInvariant() }, null!, 50);
        Assert.AreEqual(1, byUppercasePrefix.Events.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = eventId[4..12];
        var byMidFragment = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = midFragment }, null!, 50);
        Assert.AreEqual(0, byMidFragment.Events.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    [TestMethod]
    public async Task SearchAudits_matches_auditor_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var auditor = Id("prefix-alice");
        await store.StoreMessageAudit(Id("evt-pa1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(Id("evt-pa2"), new MessageAuditEntity { AuditorName = Id("someone-else"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });

        var prefix = auditor[..(_scope.Length + 8)];
        var byPrefix = await store.SearchAudits(new AuditFilter { AuditorName = prefix }, null, 50);
        Assert.AreEqual(1, byPrefix.Audits.Count(), "A leading fragment of the auditor name must match (prefix semantics).");

        var byUppercasePrefix = await store.SearchAudits(new AuditFilter { AuditorName = prefix.ToUpperInvariant() }, null, 50);
        Assert.AreEqual(1, byUppercasePrefix.Audits.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = auditor[4..12];
        var byMidFragment = await store.SearchAudits(new AuditFilter { AuditorName = midFragment }, null, 50);
        Assert.AreEqual(0, byMidFragment.Audits.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    // ───── Search projection contract: everything round-trips EXCEPT EventJson ─────

    /// <summary>
    /// Drift guard (load-bearing): reflects over every <see cref="UnresolvedEvent"/>
    /// property so that a future property silently missing from a provider's search
    /// projection (e.g. the Cosmos server-side member-init) fails here instead of
    /// shipping truncated search results.
    /// </summary>
    [TestMethod]
    public async Task GetEventsByFilter_roundtrips_every_property_except_EventJson()
    {
        var store = CreateStore();
        var endpointId = Id("ep-drift");
        var eventId = Id("drift-1");
        const string sessionId = "session-drift";

        var stored = FullySetEvent(endpointId, eventId, sessionId);
        await store.UploadFailedMessage(eventId, sessionId, endpointId, stored);

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = eventId }, null!, 50);
        var fetched = resp.Events.Single();

        // Provider-stamped on upload — value equality is not part of the contract.
        var exempt = new HashSet<string> { nameof(UnresolvedEvent.UpdatedAt), nameof(UnresolvedEvent.ResolutionStatus) };

        foreach (var prop in typeof(UnresolvedEvent).GetProperties())
        {
            if (exempt.Contains(prop.Name)) continue;

            if (prop.Name == nameof(UnresolvedEvent.MessageContent))
            {
                Assert.IsNull(fetched.MessageContent?.EventContent?.EventJson,
                    "Search results must omit the heavy EventJson payload.");
                Assert.AreEqual(stored.MessageContent.EventContent.EventTypeId, fetched.MessageContent?.EventContent?.EventTypeId,
                    "EventContent.EventTypeId must survive the search projection.");
                Assert.AreEqual(stored.MessageContent.ErrorContent.ErrorText, fetched.MessageContent?.ErrorContent?.ErrorText,
                    "ErrorContent must survive the search projection (the error-grouped view reads it).");
                Assert.AreEqual(stored.MessageContent.ErrorContent.ErrorType, fetched.MessageContent?.ErrorContent?.ErrorType);
                Assert.AreEqual(stored.MessageContent.ErrorContent.ExceptionStackTrace, fetched.MessageContent?.ErrorContent?.ExceptionStackTrace);
                continue;
            }

            // DateTime values compare by ticks (Kind-insensitive) — SQL Server
            // returns datetime2 as Kind=Unspecified with unchanged UTC ticks.
            var expectedValue = prop.GetValue(stored);
            var actualValue = prop.GetValue(fetched);
            Assert.AreEqual(expectedValue, actualValue,
                $"UnresolvedEvent.{prop.Name} did not round-trip through search — is it missing from a provider's search projection?");
        }
    }

    [TestMethod]
    public async Task Search_results_omit_EventJson_without_corrupting_the_stored_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep-nomut");
        var eventId = Id("nomut-1");
        const string sessionId = "session-nomut";

        await store.UploadFailedMessage(eventId, sessionId, endpointId, FullySetEvent(endpointId, eventId, sessionId));

        var searched = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = eventId }, null!, 50);
        Assert.IsNull(searched.Events.Single().MessageContent?.EventContent?.EventJson);

        var direct = await store.GetFailedEvent(endpointId, eventId, sessionId);
        Assert.AreEqual("{\"secret\":\"payload\"}", direct.MessageContent?.EventContent?.EventJson,
            "Stripping EventJson from SEARCH results must not corrupt the stored event (in-memory must clone).");
    }

    /// <summary>
    /// Drift guard for the message search projection: reflects over every
    /// <see cref="MessageEntity"/> property. See the Cosmos
    /// <c>MessageSearchProjection</c> constant.
    /// </summary>
    [TestMethod]
    public async Task SearchMessages_roundtrips_every_property_except_EventJson()
    {
        var store = CreateStore();
        var endpointId = Id("ep-msgdrift");
        var eventId = Id("msgdrift-1");

        var stored = new MessageEntity
        {
            EventId = eventId,
            MessageId = Id("msgdrift-m1"),
            EventTypeId = "OrderPlaced",
            OriginatingMessageId = "origin-1",
            ParentMessageId = "parent-1",
            From = "publisher-1",
            To = "subscriber-1",
            OriginatingFrom = "adapter-1",
            SessionId = "session-msgdrift",
            CorrelationId = "corr-msgdrift",
            EnqueuedTimeUtc = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc),
            MessageType = MessageType.ErrorResponse,
            EndpointRole = EndpointRole.Subscriber,
            EndpointId = endpointId,
            RetryCount = 3,
            RetryLimit = 7,
            DeadLetterReason = "dl-reason",
            DeadLetterErrorDescription = "dl-description",
            OriginalSessionId = "orig-session",
            DeferralSequence = 5,
            QueueTimeMs = 111,
            ProcessingTimeMs = 222,
            PendingSubStatus = "Handoff",
            HandoffReason = "external work",
            ExternalJobId = "JOB-9",
            ExpectedBy = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            CloudEventId = "ce-message-search-1",
            CloudEventSource = "urn:nimbus:message-search",
            CloudEventType = "com.nimbus.message-search.v1",
            CloudEventSubject = "messages/42",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{\"secret\":\"payload\"}" },
                ErrorContent = new ErrorContent { ErrorText = "boom", ErrorType = "System.InvalidOperationException", ExceptionStackTrace = "at X" },
            },
        };
        await store.StoreMessage(stored);

        var resp = await store.SearchMessages(new MessageFilter { EventId = eventId }, null, 50);
        var fetched = resp.Messages.Single();

        // Handoff metadata is carried on the UnresolvedEvents row, not the
        // per-message history row — the SQL provider's Messages table has no
        // columns for these four, so their round-trip is not part of the
        // message-search contract. (Covered for events by the UnresolvedEvent
        // drift guard above.)
        var exempt = new HashSet<string>
        {
            nameof(MessageEntity.PendingSubStatus),
            nameof(MessageEntity.HandoffReason),
            nameof(MessageEntity.ExternalJobId),
            nameof(MessageEntity.ExpectedBy),
        };

        foreach (var prop in typeof(MessageEntity).GetProperties())
        {
            if (exempt.Contains(prop.Name)) continue;

            if (prop.Name == nameof(MessageEntity.MessageContent))
            {
                Assert.IsNull(fetched.MessageContent?.EventContent?.EventJson,
                    "Message search results must omit the heavy EventJson payload.");
                Assert.AreEqual(stored.MessageContent.EventContent.EventTypeId, fetched.MessageContent?.EventContent?.EventTypeId);
                Assert.AreEqual(stored.MessageContent.ErrorContent.ErrorText, fetched.MessageContent?.ErrorContent?.ErrorText,
                    "ErrorContent must survive the message search projection.");
                continue;
            }

            // DateTime values compare by ticks (Kind-insensitive) — see the
            // UnresolvedEvent drift guard.
            var expectedValue = prop.GetValue(stored);
            var actualValue = prop.GetValue(fetched);
            Assert.AreEqual(expectedValue, actualValue,
                $"MessageEntity.{prop.Name} did not round-trip through message search — is it missing from a provider's projection?");
        }
    }

    private static UnresolvedEvent FullySetEvent(string endpointId, string eventId, string sessionId) => new()
    {
        UpdatedAt = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
        EnqueuedTimeUtc = new DateTime(2026, 5, 1, 7, 59, 0, DateTimeKind.Utc),
        EventId = eventId,
        SessionId = sessionId,
        CorrelationId = "corr-drift",
        ResolutionStatus = ResolutionStatus.Failed,
        EndpointRole = EndpointRole.Subscriber,
        EndpointId = endpointId,
        RetryCount = 3,
        RetryLimit = 7,
        MessageType = MessageType.ErrorResponse,
        DeadLetterReason = "dl-reason",
        DeadLetterErrorDescription = "dl-description",
        LastMessageId = "last-1",
        OriginatingMessageId = "origin-1",
        ParentMessageId = "parent-1",
        Reason = "reason-1",
        OriginatingFrom = "adapter-1",
        EventTypeId = "OrderPlaced",
        To = "subscriber-1",
        From = "publisher-1",
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{\"secret\":\"payload\"}" },
            ErrorContent = new ErrorContent { ErrorText = "boom", ErrorType = "System.InvalidOperationException", ExceptionStackTrace = "at X" },
        },
        QueueTimeMs = 111,
        ProcessingTimeMs = 222,
        PendingSubStatus = "Handoff",
        HandoffReason = "external work",
        ExternalJobId = "JOB-9",
        ExpectedBy = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        CloudEventId = "ce-event-search-1",
        CloudEventSource = "urn:nimbus:event-search",
        CloudEventType = "com.nimbus.event-search.v1",
        CloudEventSubject = "events/42",
    };
}
