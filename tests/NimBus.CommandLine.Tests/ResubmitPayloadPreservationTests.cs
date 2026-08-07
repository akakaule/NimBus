#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// `nb` resubmit walks an endpoint with a filter search, whose results omit
/// EventJson by cross-provider contract. These tests pin that the CLI re-reads
/// the full event before it writes or resubmits, so a marked (spec 025) timeout
/// reaches its handler with the payload, marker, and workflow conversation ID
/// intact — a handler cannot re-read durable workflow state for a message it
/// could not even deserialize, and the stored payload must survive the status
/// update.
/// </summary>
[Collection("Console output")]
public sealed class ResubmitPayloadPreservationTests
{
    private const string Endpoint = "Payments";
    private const string EventId = "event-1";
    private const string SessionId = "order-42";
    private const string TimeoutId = "order-42:payment-timeout:1";
    private const string EventJson = "{\"OrderId\":\"order-42\"}";

    [Fact]
    public async Task UpdateMessagesAndResubmit_MarkedTimeout_PreservesPayloadMarkerAndWorkflowCorrelation()
    {
        var store = new PayloadStrippingSearchStore(AgedTimeoutEvent());
        var sender = new CapturingSender();

        await Container.UpdateMessagesAndResubmit(sender, store, Endpoint, ResolutionStatus.Failed);

        var resubmitted = Assert.IsType<Message>(Assert.Single(sender.Sent));
        Assert.Equal(EventJson, resubmitted.MessageContent.EventContent.EventJson);
        Assert.Equal(TimeoutId, resubmitted.ScheduledMessageId);
        Assert.Equal("workflow-conversation", resubmitted.CorrelationId);
        Assert.Equal(MessageType.ResubmissionRequest, resubmitted.MessageType);
        Assert.Equal(SessionId, resubmitted.SessionId);
        Assert.Equal("PaymentTimedOut", resubmitted.EventTypeId);
    }

    [Fact]
    public async Task UpdateMessagesAndResubmit_DoesNotOverwriteTheStoredPayloadWithTheSearchProjection()
    {
        var store = new PayloadStrippingSearchStore(AgedTimeoutEvent());

        await Container.UpdateMessagesAndResubmit(new CapturingSender(), store, Endpoint, ResolutionStatus.Failed);

        var written = Assert.Single(store.Uploaded);
        Assert.Equal(EventJson, written.MessageContent.EventContent.EventJson);
        Assert.Equal(TimeoutId, written.ScheduledMessageId);
        Assert.Equal(ResolutionStatus.Failed, written.ResolutionStatus);
    }

    [Fact]
    public async Task UpdateMessagesAndResubmit_SameEventIdOnTwoSessions_ResubmitsEachSessionsOwnPayload()
    {
        // An EventId is unique per SESSION, not per endpoint: the Cosmos document ID
        // is eventId_sessionId. Re-reading by (endpoint, eventId) alone returns an
        // arbitrary one of the siblings, so the loop can update and resubmit session
        // A's payload and timeout identity while processing session B — and leave B
        // untouched. The re-read must be scoped to the row the search actually found.
        var sessionA = AgedTimeoutEvent();
        var sessionB = AgedTimeoutEvent();
        sessionB.SessionId = "order-99";
        sessionB.ScheduledMessageId = "order-99:payment-timeout:1";
        sessionB.WorkflowCorrelationId = "workflow-conversation-99";
        sessionB.MessageContent.EventContent.EventJson = "{\"OrderId\":\"order-99\"}";

        var store = new PayloadStrippingSearchStore(sessionA, sessionB);
        var sender = new CapturingSender();

        await Container.UpdateMessagesAndResubmit(sender, store, Endpoint, ResolutionStatus.Failed);

        Assert.Equal(2, sender.Sent.Count);
        var bySession = sender.Sent.Cast<Message>().ToDictionary(m => m.SessionId);
        Assert.Equal(EventJson, bySession[SessionId].MessageContent.EventContent.EventJson);
        Assert.Equal(TimeoutId, bySession[SessionId].ScheduledMessageId);
        Assert.Equal("{\"OrderId\":\"order-99\"}", bySession["order-99"].MessageContent.EventContent.EventJson);
        Assert.Equal("order-99:payment-timeout:1", bySession["order-99"].ScheduledMessageId);
        Assert.Equal("workflow-conversation-99", bySession["order-99"].CorrelationId);

        // Both siblings are updated — neither is silently skipped in favour of the other.
        Assert.Equal(
            new[] { "order-42", "order-99" },
            store.Uploaded.Select(e => e.SessionId).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task UpdateMessagesAndResubmit_TooRecentMessage_IsLeftAlone()
    {
        var recent = AgedTimeoutEvent();
        recent.UpdatedAt = DateTime.UtcNow;
        var store = new PayloadStrippingSearchStore(recent);
        var sender = new CapturingSender();

        await Container.UpdateMessagesAndResubmit(sender, store, Endpoint, ResolutionStatus.Failed);

        Assert.Empty(sender.Sent);
        Assert.Empty(store.Uploaded);
    }

    private static UnresolvedEvent AgedTimeoutEvent() => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        EndpointId = Endpoint,
        EventTypeId = "PaymentTimedOut",
        ResolutionStatus = ResolutionStatus.Failed,
        UpdatedAt = DateTime.UtcNow.AddHours(-1), // past the loop's 10-minute age gate
        // The failed leg's own conversation; a resubmitted timeout must be restored
        // to the WORKFLOW conversation instead.
        CorrelationId = "attempt-conversation",
        WorkflowCorrelationId = "workflow-conversation",
        ScheduledMessageId = TimeoutId,
        ScheduledEnqueueTimeUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        LastMessageId = "attempt-1",
        OriginatingMessageId = TimeoutId,
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "PaymentTimedOut", EventJson = EventJson },
        },
    };

    /// <summary>
    /// Models the real providers: the search projection is a clone with EventJson
    /// stripped, while the session-scoped read returns the full document. Documents
    /// are keyed the way the providers key them — by EventId AND SessionId — so a
    /// re-read that drops the session resolves ambiguously, exactly as in production.
    /// </summary>
    private sealed class PayloadStrippingSearchStore(params UnresolvedEvent[] stored) : Container.IResubmitEventStore
    {
        public List<UnresolvedEvent> Uploaded { get; } = new();

        public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount)
        {
            var matches = stored
                .Where(e => e.EndpointId == filter.EndPointId
                            && (filter.ResolutionStatus == null
                                || filter.ResolutionStatus.Contains(e.ResolutionStatus.ToString())))
                .Select(StripEventJson)
                .ToList();
            // ContinuationToken null ends the caller's paging loop after one page.
            return Task.FromResult(new SearchResponse { Events = matches });
        }

        public Task<UnresolvedEvent> GetEvent(string endpointId, string eventId, string sessionId, ResolutionStatus status)
        {
            var match = stored.FirstOrDefault(e =>
                e.EndpointId == endpointId
                && e.EventId == eventId
                && e.SessionId == sessionId
                && e.ResolutionStatus == status);
            return Task.FromResult(match is null ? null : Clone(match));
        }

        public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            Uploaded.Add(content);
            return Task.FromResult(true);
        }

        private static UnresolvedEvent StripEventJson(UnresolvedEvent source)
        {
            var clone = Clone(source);
            if (clone.MessageContent?.EventContent != null)
                clone.MessageContent.EventContent.EventJson = null;
            return clone;
        }

        private static UnresolvedEvent Clone(UnresolvedEvent source) =>
            JsonConvert.DeserializeObject<UnresolvedEvent>(JsonConvert.SerializeObject(source))!;
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(1L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
