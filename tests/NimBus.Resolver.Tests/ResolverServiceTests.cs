#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Broker.Services;
using NimBus.Core.Logging;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
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

    private static ResolverService CreateService(FakeCosmosDbClient? cosmos = null)
    {
        cosmos ??= new FakeCosmosDbClient();
        return new ResolverService(new FakeLoggerProvider(), cosmos);
    }

    private static FakeMessageContext CreateMessageContext(
        MessageType messageType,
        string to = "AnalyticsEndpoint",
        string from = "StorefrontEndpoint",
        int throttleRetryCount = 0)
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
                    EventTypeId = "OrderPlaced",
                    EventJson = "{}",
                },
            },
            EventTypeId = "OrderPlaced",
            EnqueuedTimeUtc = new DateTime(2026, 03, 06, 12, 00, 00, DateTimeKind.Utc),
            ThrottleRetryCount = throttleRetryCount,
        };
    }

    private sealed class FakeLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger = new FakeLogger();

        public ILogger GetContextualLogger(IMessageContext messageContext) => _logger;

        public ILogger GetContextualLogger(IMessage message) => _logger;

        public ILogger GetContextualLogger(string correlationId) => _logger;
    }

    private sealed class FakeLogger : ILogger
    {
        public void Verbose(string messageTemplate, params object[] propertyValues) { }
        public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Information(string messageTemplate, params object[] propertyValues) { }
        public void Information(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Error(string messageTemplate, params object[] propertyValues) { }
        public void Error(Exception exception, string messageTemplate, params object[] propertyValues) { }
        public void Fatal(string messageTemplate, params object[] propertyValues) { }
        public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) { }
    }

    private sealed class FakeMessageContext : IMessageContext
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
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }

        public int CompletedCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int ScheduleRedeliveryCalls { get; private set; }
        public TimeSpan? LastScheduledDelay { get; private set; }
        public int? LastScheduledRetryCount { get; private set; }
        public string? LastDeadLetterReason { get; private set; }

        public Task Complete(CancellationToken cancellationToken = default)
        {
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task Abandon(TransientException exception) => Task.CompletedTask;

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

    private sealed class FakeCosmosDbClient : ICosmosDbClient
    {
        public Exception? StoreMessageException { get; set; }
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
            PendingUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            DeferredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            FailedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            DeadLetteredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            UnsupportedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            SkippedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            CompletedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount) => throw new NotSupportedException();
        public Task<UnresolvedEvent> GetPendingEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
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
        public Task<IEnumerable<BlockedMessageEvent>> GetBlockedEventsOnSession(string endpointId, string sessionId) => throw new NotSupportedException();
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
        public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat() => throw new NotSupportedException();
        public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata) => throw new NotSupportedException();
        public Task EnableHeartbeatOnEndpoint(string endpointId, bool enable) => throw new NotSupportedException();
        public Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId) => throw new NotSupportedException();
        public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) => throw new NotSupportedException();
        public Task<MessageEntity> GetMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) => throw new NotSupportedException();
        public Task<MessageEntity> GetFailedMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task<MessageEntity> GetDeadletteredMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task RemoveStoredMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) => throw new NotSupportedException();
        public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => throw new NotSupportedException();
        public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => throw new NotSupportedException();

        public Task StoreMessage(MessageEntity message)
        {
            if (StoreMessageException is not null)
            {
                return Task.FromException(StoreMessageException);
            }

            StoredMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity)
        {
            StoredAudits.Add((eventId, auditEntity));
            return Task.CompletedTask;
        }
    }

    private sealed record UploadCall(string EventId, string SessionId, string EndpointId, UnresolvedEvent Content);
}


