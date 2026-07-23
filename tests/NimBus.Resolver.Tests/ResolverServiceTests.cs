#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Broker.Services;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Resolver.Tests;

[TestClass]
public class ResolverServiceTests
{
    [TestMethod]
    public void DetermineEndpoint_UsesToForRequestMessages()
    {
        var service = CreateService();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");

        var (endpointId, role) = service.DetermineEndpoint(message);

        Assert.AreEqual("BillingEndpoint", endpointId);
        Assert.AreEqual(EndpointRole.Subscriber, role);
    }

    [TestMethod]
    public void DetermineEndpoint_UsesFromForResponseMessages()
    {
        var service = CreateService();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");

        var (endpointId, role) = service.DetermineEndpoint(message);

        Assert.AreEqual("BillingEndpoint", endpointId);
        Assert.AreEqual(EndpointRole.Subscriber, role);
    }

    [TestMethod]
    public async Task Handle_EventRequest_StoresMessageUploadsPendingAndCompletes()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual("BillingEndpoint", cosmos.StoredMessages[0].EndpointId);
        Assert.AreEqual("OrderPlaced", cosmos.StoredMessages[0].EventTypeId);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual("BillingEndpoint", cosmos.PendingUploads[0].EndpointId);
        Assert.AreEqual(ResolutionStatus.Pending, cosmos.PendingUploads[0].Content.ResolutionStatus);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_DynamicallyTypedEvent_RecordsPendingThenCompleted_KeyedByEventTypeId()
    {
        // Spec 022 Phase 0: the Resolver/audit trail must work for an event identified only by a
        // dynamic EventTypeId string (e.g. "crm.contact.enriched.v1") with no compiled C# IEvent.
        // The Resolver keys purely off strings, so the agent zone's dynamically-typed events are
        // first-class in the audit trail with no special casing.
        const string dynamicEventTypeId = "crm.contact.enriched.v1";
        var cosmos = new FakeCosmosDbClient();
        var service = CreateService(cosmos);

        var request = CreateMessageContext(
            messageType: MessageType.EventRequest,
            to: "AgentZoneEndpoint",
            from: "CrmEndpoint",
            eventTypeId: dynamicEventTypeId);

        await service.Handle(request);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual(dynamicEventTypeId, cosmos.StoredMessages[0].EventTypeId, "Audit row must carry the dynamic EventTypeId.");
        Assert.AreEqual("AgentZoneEndpoint", cosmos.StoredMessages[0].EndpointId);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual(ResolutionStatus.Pending, cosmos.PendingUploads[0].Content.ResolutionStatus);

        // The subscriber's ResolutionResponse flips the same dynamic event to Completed.
        var response = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "AgentZoneEndpoint",
            eventTypeId: dynamicEventTypeId);

        await service.Handle(response);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        Assert.AreEqual("event-1", cosmos.CompletedUploads[0].EventId);
    }

    [TestMethod]
    public async Task Handle_CloudEventResponse_PersistsCloudEventIdentityOnTrackingRecord()
    {
        // AC15: the CloudEvents identity carried on the response from a CloudEvents-
        // consuming subscriber must be projected onto the tracking record (the
        // UnresolvedEvent the WebApp surfaces) and the per-message audit row.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");
        message.CloudEventId = "ce-1";
        message.CloudEventSource = "urn:ext:billing";
        message.CloudEventType = "InvoiceCreated";
        message.CloudEventSubject = "customer-42";
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        var tracked = cosmos.CompletedUploads[0].Content;
        Assert.AreEqual("ce-1", tracked.CloudEventId);
        Assert.AreEqual("urn:ext:billing", tracked.CloudEventSource);
        Assert.AreEqual("InvoiceCreated", tracked.CloudEventType);
        Assert.AreEqual("customer-42", tracked.CloudEventSubject);

        // The per-message audit row (MessageEntity) carries it too.
        Assert.AreEqual("ce-1", cosmos.StoredMessages[0].CloudEventId);
        Assert.AreEqual("InvoiceCreated", cosmos.StoredMessages[0].CloudEventType);
    }

    [TestMethod]
    public async Task Handle_NativeResponse_LeavesCloudEventIdentityNull()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.IsNull(cosmos.CompletedUploads[0].Content.CloudEventId);
        Assert.IsNull(cosmos.CompletedUploads[0].Content.CloudEventType);
        Assert.IsNull(cosmos.StoredMessages[0].CloudEventId);
    }

    [TestMethod]
    public async Task Handle_DiscardSkipResponse_UploadsSkippedOutcomeWithReason()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(
            messageType: MessageType.SkipResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint");
        message.MessageContent.ErrorContent = new ErrorContent
        {
            ErrorText = "InvalidOperationException: Known bad event version. Classified by PartnerFailureDispositionClassifier.",
            ErrorType = nameof(InvalidOperationException),
        };
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.SkippedUploads.Count);
        var tracked = cosmos.SkippedUploads[0].Content;
        Assert.AreEqual(ResolutionStatus.Skipped, tracked.ResolutionStatus);
        Assert.AreEqual(message.MessageContent.ErrorContent.ErrorText, tracked.Reason);
        Assert.AreEqual(nameof(InvalidOperationException), tracked.MessageContent.ErrorContent.ErrorType);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_RetryRequest_StoresAuditBeforePersistingMessage()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.RetryRequest, to: "BillingEndpoint", from: "Manager");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.StoredAudits.Count);
        Assert.AreEqual(message.EventId, cosmos.StoredAudits[0].EventId);
        Assert.AreEqual(MessageAuditType.Retry, cosmos.StoredAudits[0].Audit.AuditType);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_SchedulesRedeliveryWithIncrementedRetryCount()
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(retryAfter),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 2);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(20), message.LastScheduledDelay);
        Assert.AreEqual(3, message.LastScheduledRetryCount);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_StorageProviderTransientExceptionWithoutRetryAfter_UsesCalculatedBackoff()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new StorageProviderTransientException("temporarily unavailable", retryAfter: null),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 1);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(10), message.LastScheduledDelay);
        Assert.AreEqual(2, message.LastScheduledRetryCount);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_DeadLettersWhenMaxRetriesReached()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 10);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("Max throttle retries exceeded", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_TerminalAfterHandoff_OverridesProcessingTimeWithWallClock()
    {
        // Simulates the lifecycle of a handoff event:
        //   T0       — EventRequest enqueued (the wall-clock anchor)
        //   T0+200ms — PendingHandoffResponse audit row arrives
        //   ...long external job runs...
        //   now()    — Final ResolutionResponse settles the event
        // The aggregate UnresolvedEvent's ProcessingTimeMs must reflect the
        // full wall-clock span (now − EventRequest.EnqueuedTimeUtc), not the
        // 250ms handler duration of the final hop.
        var cosmos = new FakeCosmosDbClient();
        var eventRequestEnqueued = DateTime.UtcNow.AddSeconds(-30);

        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = eventRequestEnqueued,
            EndpointId = "BillingEndpoint",
        });
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-pending-handoff",
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = eventRequestEnqueued.AddMilliseconds(200),
            EndpointId = "BillingEndpoint",
            PendingSubStatus = "Handoff",
        });

        var resolutionResponse = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        resolutionResponse.ProcessingTimeMs = 250; // raw last-hop handler duration

        var service = CreateService(cosmos);
        await service.Handle(resolutionResponse);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count, "Terminal upload should have run.");
        var completed = cosmos.CompletedUploads[0].Content;
        Assert.IsNotNull(completed.ProcessingTimeMs, "Wall-clock value must be populated.");
        Assert.IsTrue(completed.ProcessingTimeMs >= 30_000,
            $"Expected wall-clock ≥ 30s; got {completed.ProcessingTimeMs}ms.");
        Assert.IsTrue(completed.ProcessingTimeMs < 60_000,
            $"Wall-clock should be the EventRequest→now span, not far longer; got {completed.ProcessingTimeMs}ms.");

        // Per-message audit row preserves the raw last-hop duration.
        var resolutionRow = cosmos.StoredMessages.Single(m => m.MessageType == MessageType.ResolutionResponse);
        Assert.AreEqual(250, resolutionRow.ProcessingTimeMs);
    }

    [TestMethod]
    public async Task Handle_TerminalWithoutHandoff_KeepsHandlerProcessingTime()
    {
        // Non-handoff event: history has only the EventRequest. The override
        // must not trigger; the response's local handler duration (75ms) wins.
        var cosmos = new FakeCosmosDbClient();
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-30),
            EndpointId = "BillingEndpoint",
        });

        var resolutionResponse = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        resolutionResponse.ProcessingTimeMs = 75;

        var service = CreateService(cosmos);
        await service.Handle(resolutionResponse);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        Assert.AreEqual(75, cosmos.CompletedUploads[0].Content.ProcessingTimeMs);
    }

    [TestMethod]
    public async Task Handle_HandoffFailure_OverridesProcessingTimeOnErrorResponse()
    {
        // HandoffFailedRequest path: the terminal ErrorResponse must also use
        // the wall-clock span. Symmetric with the success path above.
        var cosmos = new FakeCosmosDbClient();
        var eventRequestEnqueued = DateTime.UtcNow.AddSeconds(-30);

        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = eventRequestEnqueued,
            EndpointId = "BillingEndpoint",
        });
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-pending-handoff",
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = eventRequestEnqueued.AddMilliseconds(200),
            EndpointId = "BillingEndpoint",
            PendingSubStatus = "Handoff",
        });

        var errorResponse = CreateMessageContext(
            messageType: MessageType.ErrorResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        errorResponse.ProcessingTimeMs = 50;

        var service = CreateService(cosmos);
        await service.Handle(errorResponse);

        Assert.AreEqual(1, cosmos.FailedUploads.Count);
        Assert.IsTrue(cosmos.FailedUploads[0].Content.ProcessingTimeMs >= 30_000);
    }

    [TestMethod]
    public async Task Handle_UnexpectedException_DeadLettersMessage()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new InvalidOperationException("boom"),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("Failed to handle message.", message.LastDeadLetterReason);
        Assert.AreEqual(0, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_StoreCancellation_RethrowsWithoutSettlingMessage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new OperationCanceledException(cancellation.Token),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(cosmos);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_NotificationCancellation_RethrowsWithoutCompletingOrDeadLettering()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var notifier = new ThrowingMessageStateChangeNotifier(
            new OperationCanceledException(cancellation.Token));
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(notifier: notifier);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_CompletionCancellation_RethrowsWithoutDeadLettering()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        message.CompleteException = new OperationCanceledException(cancellation.Token);
        var service = CreateService();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    private static ResolverService CreateService(
        FakeCosmosDbClient? cosmos = null,
        IMessageStateChangeNotifier? notifier = null)
    {
        cosmos ??= new FakeCosmosDbClient();
        return new ResolverService(cosmos, notifier ?? new NoopMessageStateChangeNotifier());
    }

    private static FakeMessageContext CreateMessageContext(
        MessageType messageType,
        string to = "AnalyticsEndpoint",
        string from = "StorefrontEndpoint",
        int throttleRetryCount = 0,
        string eventTypeId = "OrderPlaced")
    {
        return new FakeMessageContext
        {
            EventId = "event-1",
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            SessionId = "session-1",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = to,
            MessageType = messageType,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}",
                },
            },
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = new DateTime(2026, 03, 06, 12, 00, 00, DateTimeKind.Utc),
            ThrottleRetryCount = throttleRetryCount,
        };
    }

    internal sealed class FakeMessageContext : IMessageContext
    {
        public string EventId { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public string From { get; set; } = string.Empty;
        public string DeadLetterReason { get; set; } = null!;
        public string DeadLetterErrorDescription { get; set; } = null!;
        public string HandoffReason { get; set; }
        public string ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata HandoffMetadata { get; set; }
        public string CloudEventId { get; set; }
        public string CloudEventSource { get; set; }
        public string CloudEventType { get; set; }
        public string CloudEventSubject { get; set; }

        public int CompletedCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int ScheduleRedeliveryCalls { get; private set; }
        public TimeSpan? LastScheduledDelay { get; private set; }
        public int? LastScheduledRetryCount { get; private set; }
        public string? LastDeadLetterReason { get; private set; }
        public Exception? CompleteException { get; set; }

        public Task Complete(CancellationToken cancellationToken = default)
        {
            CompletedCalls++;
            return CompleteException is null
                ? Task.CompletedTask
                : Task.FromException(CompleteException);
        }

        public Task Abandon(TransientException exception)
        {
            AbandonCalls++;
            return Task.CompletedTask;
        }

        public Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default)
        {
            DeadLetterCalls++;
            LastDeadLetterReason = reason;
            return Task.CompletedTask;
        }

        public Task Defer(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeferOnly(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IMessageContext> ReceiveNextDeferred(CancellationToken cancellationToken = default) => Task.FromResult<IMessageContext>(this);
        public Task<IMessageContext> ReceiveNextDeferredWithPop(CancellationToken cancellationToken = default) => Task.FromResult<IMessageContext>(this);
        public Task BlockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
        {
            ScheduleRedeliveryCalls++;
            LastScheduledDelay = delay;
            LastScheduledRetryCount = throttleRetryCount;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingMessageStateChangeNotifier : IMessageStateChangeNotifier
    {
        private readonly Exception _exception;

        public ThrowingMessageStateChangeNotifier(Exception exception)
        {
            _exception = exception;
        }

        public Task NotifyEndpointStateChangedAsync(
            string endpointId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(_exception);
    }

    internal sealed class FakeCosmosDbClient : ICosmosDbClient, NimBus.MessageStore.Abstractions.INimBusMessageStore
    {
        public Exception? StoreMessageException { get; set; }
        public Exception? UploadException { get; set; }
        public Exception? StoreAuditException { get; set; }
        public List<MessageEntity> StoredMessages { get; } = new();
        public List<(string EventId, MessageAuditEntity Audit)> StoredAudits { get; } = new();
        public List<UploadCall> PendingUploads { get; } = new();
        public List<UploadCall> DeferredUploads { get; } = new();
        public List<UploadCall> FailedUploads { get; } = new();
        public List<UploadCall> DeadLetteredUploads { get; } = new();
        public List<UploadCall> UnsupportedUploads { get; } = new();
        public List<UploadCall> CompletedUploads { get; } = new();
        public List<UploadCall> SkippedUploads { get; } = new();

        public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            PendingUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            DeferredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            FailedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            DeadLetteredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            UnsupportedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            SkippedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            CompletedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetFailedEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetDeferredEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetEvent(string endpointId, string eventId) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetEventById(string endpointId, string eventId) => throw new NotSupportedException();
        public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds) => throw new NotSupportedException();
        public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) => throw new NotSupportedException();
        public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) => throw new NotSupportedException();
        public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) => throw new NotSupportedException();
        public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) => throw new NotSupportedException();
        public Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) => throw new NotSupportedException();
        public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) => throw new NotSupportedException();
        public Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId) => throw new NotSupportedException();
        public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) => throw new NotSupportedException();
        public Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency) => throw new NotSupportedException();
        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId) => throw new NotSupportedException();
        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText) => throw new NotSupportedException();
        public Task<string> GetEndpointErrorList(string endpointId) => throw new NotSupportedException();
        public Task<bool> UpdateSubscription(EndpointSubscription subscription) => throw new NotSupportedException();
        public Task<bool> UnsubscribeById(string endpointId, string mail) => throw new NotSupportedException();
        public Task<bool> DeleteSubscription(string subscriptionId) => throw new NotSupportedException();
        public Task<bool> UnsubscribeByMail(string endpointId, string mail) => throw new NotSupportedException();
        public Task<bool> PurgeMessages(string endpointId, string sessionId) => throw new NotSupportedException();
        public Task<bool> PurgeMessages(string endpointId) => throw new NotSupportedException();
        public Task<EndpointMetadata> GetEndpointMetadata(string endpointId) => throw new NotSupportedException();
        public Task<List<EndpointMetadata>> GetMetadatas() => throw new NotSupportedException();
        public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds) => throw new NotSupportedException();
        public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata) => throw new NotSupportedException();
        public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) => throw new NotSupportedException();
        public Task<MessageEntity> GetMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) =>
            Task.FromResult<IEnumerable<MessageEntity>>(StoredMessages.Where(m => m.EventId == eventId).ToList());
        public Task<MessageEntity> GetLatestEventRequestMessage(string eventId) =>
            Task.FromResult(StoredMessages
                .Where(m => m.EventId == eventId
                         && (m.MessageType == MessageType.EventRequest || m.MessageType == MessageType.ResubmissionRequest)
                         && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
                .OrderByDescending(m => m.EnqueuedTimeUtc)
                .FirstOrDefault());
        public Task<MessageEntity> GetFailedMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task RemoveStoredMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) => throw new NotSupportedException();
        public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => throw new NotSupportedException();
        public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => throw new NotSupportedException();
        public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => throw new NotSupportedException();
        public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => throw new NotSupportedException();
        public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => throw new NotSupportedException();

        public Task StoreMessage(MessageEntity message)
        {
            if (StoreMessageException is not null)
            {
                return Task.FromException(StoreMessageException);
            }

            StoredMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
        {
            if (StoreAuditException is not null) return Task.FromException(StoreAuditException);
            StoredAudits.Add((eventId, auditEntity));
            return Task.CompletedTask;
        }

        public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, System.Collections.Generic.IReadOnlyCollection<string> eventIds) => throw new NotSupportedException();

        public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyDictionary<string, NimBus.MessageStore.States.EventReport>> GetEventReports(string endpointId, System.Collections.Generic.IReadOnlyCollection<string> eventIds) => throw new NotSupportedException();

        public Task<NimBus.MessageStore.States.EventSchema?> GetSchema(string eventTypeId) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<NimBus.MessageStore.States.EventSchema>> GetSchemas() => throw new NotSupportedException();
        public Task<NimBus.MessageStore.States.EventSchema> DefineEventType(NimBus.MessageStore.States.EventSchema schema) => throw new NotSupportedException();
    }

    internal sealed record UploadCall(string EventId, string SessionId, string EndpointId, UnresolvedEvent Content);
}


