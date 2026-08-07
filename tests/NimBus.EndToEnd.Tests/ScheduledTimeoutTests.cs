#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Broker.Services;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.Manager;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.Testing.Conformance;
using System.ComponentModel.DataAnnotations;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 025 end-to-end coverage: a scheduled workflow timeout keeps its logical
/// identity (ScheduledMessageId) and workflow conversation ID across handler
/// failures, RetryRequest redeliveries, and operator resubmission — visible to
/// the typed handler through IEventHandlerContext.
/// </summary>
[TestClass]
public class ScheduledTimeoutTests
{
    private static readonly DateTimeOffset Due = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private const string TimeoutId = "order-42:payment-timeout:1";

    [TestMethod]
    public async Task ScheduledTimeout_FailsTwice_RetriesKeepIdentityAndWorkflowConversation()
    {
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("PaymentTimedOut", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1),
        });
        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingTimeoutHandler { FailuresBeforeSuccess = 2 };
        fixture.RegisterHandler<PaymentTimedOut>(() => handler);

        await fixture.Publisher.Schedule(
            new PaymentTimedOut { OrderId = "order-42" },
            Due,
            WorkflowContext(),
            TimeoutId);

        // First delivery fails.
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(1, handler.Invocations.Count);
        var retry1 = RedeliverRetry(fixture, "retry-attempt-2");

        Assert.AreEqual(TimeoutId, retry1.ScheduledMessageId, "The retry clone carries the marker");
        Assert.AreEqual("workflow-conversation", retry1.CorrelationId,
            "A retry clone of a marked timeout preserves the workflow conversation ID");

        // Second delivery (first retry) fails again.
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(2, handler.Invocations.Count);
        var retry2 = RedeliverRetry(fixture, "retry-attempt-3");
        Assert.AreEqual(TimeoutId, retry2.ScheduledMessageId);
        Assert.AreEqual("workflow-conversation", retry2.CorrelationId,
            "Second and later retries keep the workflow conversation ID by induction");

        // Third delivery (second retry) succeeds — the handler still sees the
        // ORIGINAL logical identity, never the per-attempt MessageId.
        await fixture.DeliverAll();
        Assert.AreEqual(3, handler.Invocations.Count);
        var final = handler.Invocations[^1];
        Assert.AreEqual(TimeoutId, final.ScheduledMessageId);
        Assert.AreEqual(Due, final.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("workflow-conversation", final.CorrelationId);
        Assert.AreEqual("retry-attempt-3", final.MessageId,
            "The per-attempt transport MessageId differs from the logical identity");
    }

    [TestMethod]
    public async Task ScheduledTimeout_FirstDelivery_MessageIdEqualsTimeoutId()
    {
        var fixture = new EndToEndFixture();
        var handler = new RecordingTimeoutHandler();
        fixture.RegisterHandler<PaymentTimedOut>(() => handler);

        await fixture.Publisher.Schedule(
            new PaymentTimedOut { OrderId = "order-42" }, Due, WorkflowContext(), TimeoutId);
        await fixture.DeliverAll();

        var invocation = handler.Invocations.Single();
        Assert.AreEqual(TimeoutId, invocation.MessageId,
            "TimeoutId is the deterministic MessageId of the FIRST delivery");
        Assert.AreEqual(TimeoutId, invocation.ScheduledMessageId);
        Assert.AreEqual("order-42", invocation.SessionId);
    }

    [TestMethod]
    public async Task TerminalFailureThroughTheRealAuditChain_ResubmitsWithTheOriginalTimeoutIdentity()
    {
        // The whole terminal-failure -> operator-resubmit story, end to end, with
        // nothing about the resubmission hand-built: the scheduled timeout fails in
        // the handler, ResponseService emits the ErrorResponse, the real
        // ResolverService persists the audit chain, the real ManagerClient rebuilds
        // the ResubmissionRequest FROM WHAT WAS PERSISTED, and that broker message is
        // delivered back through the wire round trip. Every link that could drop the
        // timeout identity is exercised, not assumed.
        var fixture = new EndToEndFixture();
        var handler = new RecordingTimeoutHandler { FailuresBeforeSuccess = 1 };
        fixture.RegisterHandler<PaymentTimedOut>(() => handler);

        await fixture.Publisher.Schedule(
            new PaymentTimedOut { OrderId = "order-42" }, Due, WorkflowContext(), TimeoutId);
        var timeoutRequest = fixture.PublishBus.SentMessages.Single();
        await fixture.DeliverAllWithResults();

        var errorResponse = fixture.ResponseBus.SentMessages.Single(m => m.MessageType == MessageType.ErrorResponse);
        Assert.AreEqual(TimeoutId, errorResponse.ScheduledMessageId,
            "Link 1 — ResponseService carries the marker onto the failure response");
        Assert.AreEqual("workflow-conversation", errorResponse.WorkflowCorrelationId,
            "Link 1 — and the workflow conversation ID, which the response's own CorrelationId cannot hold");

        // Link 2 + 3 — the real Resolver, over real MessageContexts, into a store.
        // The resolver sees BOTH legs of the conversation, exactly as in production:
        // the EventRequest (whose audit row carries the payload an operator
        // resubmits) and then the ErrorResponse that terminalizes it. A response
        // clone never reuses the inbound transport MessageId — the broker assigns one
        // on the wire — and both legs share the EventId the subscription rule stamped.
        const string EventId = "event-1";
        ((Message)timeoutRequest).EventId = EventId;
        ((Message)errorResponse).EventId = EventId;
        ((Message)errorResponse).MessageId = "error-response-1";

        var store = new InMemoryMessageStore();
        var resolverBus = new InMemoryBus();
        await resolverBus.Send(timeoutRequest);
        await resolverBus.Send(errorResponse);
        var delivered = await resolverBus.DeliverAllWithResults(new ResolverService(store));

        Assert.IsFalse(delivered.Exists(d => d.Session.WasDeadLettered),
            $"The resolver must accept both legs: {delivered.Find(d => d.Session.WasDeadLettered)?.Session.LastDeadLetterDescription}");

        // The resolver keys a response row on the endpoint it came FROM — read it
        // back the same way, from the context it actually saw.
        var resolved = delivered[^1].Context;
        var auditRow = await store.GetFailedMessage(EventId, resolved.From);
        Assert.IsNotNull(auditRow, "The resolver persisted the failed message on the subscriber's endpoint");
        Assert.AreEqual(TimeoutId, auditRow.ScheduledMessageId, "Link 3 — persistence keeps the timeout identity");
        Assert.AreEqual(Due, auditRow.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("workflow-conversation", auditRow.WorkflowCorrelationId);

        var storedEvent = await store.GetFailedEvent(resolved.From, EventId, "order-42");
        Assert.AreEqual(TimeoutId, storedEvent.ScheduledMessageId,
            "Link 3 — the UnresolvedEvent an operator resubmits from keeps it too");

        // Link 4 — the real ManagerClient, fed only what the store returned: the
        // failed audit row for identity, the persisted request row for the payload
        // (the WebApp resubmit path's exact two sources).
        var payloadRow = await store.GetLatestEventRequestMessage(EventId);
        Assert.IsNotNull(payloadRow?.MessageContent?.EventContent?.EventJson,
            "Link 3 — the request leg's audit row is where the resubmit payload comes from");

        var brokerClient = new RecordingManagerServiceBusClient();
        await new ManagerClient(brokerClient).Resubmit(
            auditRow,
            "PaymentTimedOut",
            auditRow.EventTypeId,
            payloadRow.MessageContent.EventContent.EventJson);

        // Link 5 — its actual broker message goes back through the wire. The manager
        // deliberately mints no transport MessageId (reusing one would trip duplicate
        // detection); the broker assigns it, as with every response clone here.
        var wire = brokerClient.Sender.SentMessages.Single();
        Assert.IsNull(wire.MessageId, "The manager leaves the transport MessageId to the broker");
        wire.MessageId = "resubmit-attempt-1";
        await fixture.DeliverWireMessage(wire);

        Assert.AreEqual(2, handler.Invocations.Count, "The resubmission reached the handler");
        var resubmitted = handler.Invocations[^1];
        Assert.AreEqual(TimeoutId, resubmitted.ScheduledMessageId,
            "The typed handler's workflow guard sees the ORIGINAL logical identity after a full audit-chain round trip");
        Assert.AreEqual(Due, resubmitted.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("workflow-conversation", resubmitted.CorrelationId,
            "The resubmission is restored to the workflow conversation, not the failed attempt's");
        Assert.AreEqual("order-42", resubmitted.SessionId);
    }

    [TestMethod]
    public async Task OrdinaryEvent_HandlerSeesNoScheduledIdentity()
    {
        var fixture = new EndToEndFixture();
        var handler = new RecordingTimeoutHandler();
        fixture.RegisterHandler<PaymentTimedOut>(() => handler);

        await fixture.Publisher.Publish(new PaymentTimedOut { OrderId = "order-42" });
        await fixture.DeliverAll();

        Assert.IsNull(handler.Invocations.Single().ScheduledMessageId);
        Assert.IsNull(handler.Invocations.Single().ScheduledEnqueueTimeUtc);
    }

    // ── Durable workflow state decides Fired vs IgnoredLate (spec 025 AC3) ──
    //
    // NimBus guarantees at-least-once delivery and cannot cancel a timeout that
    // already activated, so the handler's re-read of durable workflow state is
    // the ONLY authority. These tests deliver the awkward cases end to end.

    [TestMethod]
    public async Task DuplicateTimeoutDelivery_FiresOnceThenIgnoresTheDuplicateAsLate()
    {
        var fixture = new EndToEndFixture();
        var workflow = new WorkflowState { CurrentGeneration = 1 };
        fixture.RegisterHandler<PaymentTimedOut>(() => new GuardedTimeoutHandler(workflow));

        var timeout = TimeoutMessage(Generation(1));
        await fixture.PublishBus.Send(timeout);
        await fixture.DeliverAll();
        // The broker redelivers the very same timeout (at-least-once); a real
        // redelivery keeps the marker and mints a new transport MessageId.
        await fixture.PublishBus.Send(TimeoutMessage(Generation(1), messageId: "redelivery-1"));
        await fixture.DeliverAll();

        CollectionAssert.AreEqual(
            new[] { ScheduledMessageHandlingOutcome.Fired, ScheduledMessageHandlingOutcome.IgnoredLate },
            workflow.Outcomes.ToArray(),
            "The workflow-state CAS fires the first delivery and absorbs the duplicate");
        Assert.AreEqual(1, workflow.FiredGenerations.Count);
    }

    [TestMethod]
    public async Task SupersededTimeoutGeneration_IsIgnoredAsLate_AndTheCurrentOneStillFires()
    {
        // The workflow rescheduled (extension, retry, human intervention), so
        // generation 1 is stale by the time it arrives — exactly the case a
        // broker cancel is allowed to lose.
        var fixture = new EndToEndFixture();
        var workflow = new WorkflowState { CurrentGeneration = 2 };
        fixture.RegisterHandler<PaymentTimedOut>(() => new GuardedTimeoutHandler(workflow));

        await fixture.PublishBus.Send(TimeoutMessage(Generation(1)));
        await fixture.PublishBus.Send(TimeoutMessage(Generation(2)));
        await fixture.DeliverAll();

        CollectionAssert.AreEqual(
            new[] { ScheduledMessageHandlingOutcome.IgnoredLate, ScheduledMessageHandlingOutcome.Fired },
            workflow.Outcomes.ToArray());
        CollectionAssert.AreEqual(new[] { Generation(2) }, workflow.FiredGenerations.ToArray());
    }

    [TestMethod]
    public async Task TimeoutArrivingAfterTheWorkflowCompleted_IsIgnoredAsLate()
    {
        var fixture = new EndToEndFixture();
        var workflow = new WorkflowState { CurrentGeneration = 1, Completed = true };
        fixture.RegisterHandler<PaymentTimedOut>(() => new GuardedTimeoutHandler(workflow));

        await fixture.PublishBus.Send(TimeoutMessage(Generation(1)));
        await fixture.DeliverAll();

        CollectionAssert.AreEqual(
            new[] { ScheduledMessageHandlingOutcome.IgnoredLate },
            workflow.Outcomes.ToArray(),
            "A completed workflow never re-applies a timeout, however it was delivered");
        Assert.AreEqual(0, workflow.FiredGenerations.Count);
    }

    private static string Generation(int generation) =>
        $"order-42:payment-timeout:{generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static Message TimeoutMessage(string timeoutId, string messageId = null) => new()
    {
        MessageId = messageId ?? timeoutId,
        CorrelationId = "workflow-conversation",
        EventId = Guid.NewGuid().ToString(),
        SessionId = "order-42",
        To = "PaymentTimedOut",
        MessageType = MessageType.EventRequest,
        EventTypeId = "PaymentTimedOut",
        ScheduledMessageId = timeoutId,
        ScheduledEnqueueTimeUtc = Due,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent
            {
                EventTypeId = "PaymentTimedOut",
                EventJson = "{\"OrderId\":\"order-42\"}",
            },
        },
    };

    /// <summary>Stands in for the process manager's durable workflow row.</summary>
    private sealed class WorkflowState
    {
        public int CurrentGeneration { get; set; }
        public bool Completed { get; set; }
        public List<ScheduledMessageHandlingOutcome> Outcomes { get; } = new();
        public List<string> FiredGenerations { get; } = new();
    }

    /// <summary>
    /// The handler shape the docs prescribe: key on ScheduledMessageId (never the
    /// per-attempt MessageId), re-read durable state, and compare-and-set.
    /// </summary>
    private sealed class GuardedTimeoutHandler(WorkflowState workflow) : IEventHandler<PaymentTimedOut>
    {
        public Task Handle(PaymentTimedOut message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            var expected = $"order-42:payment-timeout:{workflow.CurrentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var fires = !workflow.Completed
                && string.Equals(context.ScheduledMessageId, expected, StringComparison.Ordinal);

            if (fires)
            {
                workflow.Completed = true; // the CAS: this generation is consumed
                workflow.FiredGenerations.Add(context.ScheduledMessageId);
            }

            var outcome = fires ? ScheduledMessageHandlingOutcome.Fired : ScheduledMessageHandlingOutcome.IgnoredLate;
            workflow.Outcomes.Add(outcome);
            context.ReportScheduledMessageOutcome(outcome);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Pulls the RetryRequest clone off the response bus, simulates the broker
    /// assigning it a fresh transport MessageId, and re-enqueues it for delivery.
    /// </summary>
    private static Message RedeliverRetry(EndToEndFixture fixture, string brokerAssignedMessageId)
    {
        var retry = (Message)fixture.ResponseBus.SentMessages.Last(m => m.MessageType == MessageType.RetryRequest);
        Assert.IsNull(retry.MessageId, "The clone must not reuse the inbound transport MessageId");
        retry.MessageId = brokerAssignedMessageId; // broker-assigned on the real wire
        fixture.PublishBus.Send(retry).GetAwaiter().GetResult();
        return retry;
    }

    private static EventHandlerContext WorkflowContext() => new()
    {
        MessageId = "order-placed-1",
        SessionId = "order-42",
        CorrelationId = "workflow-conversation",
        ParentMessageId = Constants.Self,
        OriginatingMessageId = Constants.Self,
    };

    private sealed class PaymentTimedOut : Event
    {
        [Required]
        public string OrderId { get; set; } = string.Empty;

        public override string GetSessionId() => OrderId;
    }

    private sealed record TimeoutInvocation(
        string MessageId,
        string CorrelationId,
        string SessionId,
        string ScheduledMessageId,
        DateTimeOffset? ScheduledEnqueueTimeUtc);

    private sealed class RecordingTimeoutHandler : IEventHandler<PaymentTimedOut>
    {
        public List<TimeoutInvocation> Invocations { get; } = new();
        public int FailuresBeforeSuccess { get; init; }

        public Task Handle(PaymentTimedOut message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            Invocations.Add(new TimeoutInvocation(
                context.MessageId,
                context.CorrelationId,
                context.SessionId,
                context.ScheduledMessageId,
                context.ScheduledEnqueueTimeUtc));

            if (Invocations.Count <= FailuresBeforeSuccess)
                throw new InvalidOperationException("transient handler failure");

            return Task.CompletedTask;
        }
    }
}
