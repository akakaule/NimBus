#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
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
    public async Task ResubmittedTimeout_HandlerSeesOriginalIdentityAndWorkflowConversation()
    {
        // The final leg of the terminal-failure -> operator-resubmit story: a
        // ResubmissionRequest constructed the way ManagerClient does (marker
        // stamped, CorrelationId restored from WorkflowCorrelationId) reaches the
        // typed handler with the original logical identity intact, so the durable
        // workflow guard can decide Fired vs IgnoredLate.
        var fixture = new EndToEndFixture();
        var handler = new RecordingTimeoutHandler();
        fixture.RegisterHandler<PaymentTimedOut>(() => handler);

        var resubmission = new Message
        {
            MessageId = "resubmit-1",
            CorrelationId = "workflow-conversation",
            EventId = "event-1",
            SessionId = "order-42",
            To = "PaymentTimedOut",
            From = Constants.ManagerId,
            OriginatingMessageId = TimeoutId,
            ParentMessageId = "attempt-1",
            MessageType = MessageType.ResubmissionRequest,
            EventTypeId = "PaymentTimedOut",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "PaymentTimedOut",
                    EventJson = "{\"OrderId\":\"order-42\"}",
                },
            },
            ScheduledMessageId = TimeoutId,
            ScheduledEnqueueTimeUtc = Due,
        };

        await fixture.PublishBus.Send(resubmission);
        await fixture.DeliverAll();

        var invocation = handler.Invocations.Single();
        Assert.AreEqual(TimeoutId, invocation.ScheduledMessageId,
            "Resubmission restores the logical timeout identity from the audit chain");
        Assert.AreEqual(Due, invocation.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("workflow-conversation", invocation.CorrelationId);
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
