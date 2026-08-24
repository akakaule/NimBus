using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;

namespace NimBus.Broker.Services
{
    public class ResolverService : IMessageHandler
    {
        private readonly IMessageTrackingStore _store;
        private readonly IMessageStateChangeNotifier _notifier;
        private readonly ILogger _logger;
        private readonly IEndpointMetadataStore _metadataStore;
        private readonly IServiceHealthStore _serviceHealthStore;

        private const int MaxThrottleRetries = 10;
        private const int BaseDelaySeconds = 5;
        private const int MaxDelaySeconds = 300; // 5 minutes

        private static readonly Dictionary<MessageType, ResolutionStatus> MessageTypeToStatusMap = new()
        {
            [MessageType.EventRequest] = ResolutionStatus.Pending,
            [MessageType.ResubmissionRequest] = ResolutionStatus.Pending,
            [MessageType.RetryRequest] = ResolutionStatus.Pending,
            [MessageType.SkipRequest] = ResolutionStatus.Pending,
            [MessageType.ContinuationRequest] = ResolutionStatus.Pending,
            // PendingHandoff control flow. The response from the subscriber records
            // the audit row as Pending+Handoff; the two Manager-issued requests are
            // recorded as Pending audit rows that flip when their resulting
            // ResolutionResponse / ErrorResponse arrive (via the existing path).
            [MessageType.PendingHandoffResponse] = ResolutionStatus.Pending,
            [MessageType.HandoffCompletedRequest] = ResolutionStatus.Pending,
            [MessageType.HandoffFailedRequest] = ResolutionStatus.Pending,
            [MessageType.ErrorResponse] = ResolutionStatus.Failed,
            [MessageType.ResolutionResponse] = ResolutionStatus.Completed,
            [MessageType.DeferralResponse] = ResolutionStatus.Deferred,
            [MessageType.SkipResponse] = ResolutionStatus.Skipped,
            [MessageType.UnsupportedResponse] = ResolutionStatus.Unsupported,
        };

        /// <summary>
        /// Creates the Resolver message handler.
        /// </summary>
        /// <param name="store">Tracking store the audit trail and endpoint state are written to.</param>
        /// <param name="notifier">Write-path state-change notifier; a no-op notifier is used when omitted.</param>
        /// <param name="logger">Optional logger.</param>
        /// <param name="metadataStore">
        /// Optional heartbeat store. When absent the platform heartbeat degrades gracefully:
        /// heartbeat traffic is still diverted away from the audit trail and completed, just
        /// not recorded.
        /// </param>
        /// <param name="serviceHealthStore">
        /// Optional service-liveness store backing the Resolver's own probe. Same graceful
        /// degradation as <paramref name="metadataStore"/> when absent.
        /// </param>
        public ResolverService(
            IMessageTrackingStore store,
            IMessageStateChangeNotifier notifier = null,
            ILogger<ResolverService> logger = null,
            IEndpointMetadataStore metadataStore = null,
            IServiceHealthStore serviceHealthStore = null)
        {
            _store = store;
            _notifier = notifier ?? new NoopMessageStateChangeNotifier();
            _logger = logger;
            _metadataStore = metadataStore;
            _serviceHealthStore = serviceHealthStore;
        }

        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            _logger?.LogTrace("Resolver: Handle {EventTypeId} EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                messageContext.MessageContent.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);

            // The consumer span is owned by the transport boundary
            // (ServiceBusAdapter → NimBusConsumerInstrumentation). When invoked
            // through ServiceBusAdapter (the Azure Function `Functions.cs` path),
            // Activity.Current is the consumer span and the resolver's downstream
            // RecordOutcome / RecordAudit spans nest under it automatically. When
            // invoked directly (tests / non-adapter hosts) those resolver-side
            // spans become roots — that's expected, the resolver doesn't fabricate
            // a transport span when there isn't one.
            try
            {
                // Platform heartbeat traffic is diverted before anything touches the
                // tracking store: it is infrastructure chatter, not integration events,
                // so it must never appear on the Events / Flow / Monitor pages nor in
                // the latency aggregates.
                if (IsHeartbeat(messageContext))
                {
                    await HandleHeartbeatMessage(messageContext, cancellationToken);
                    return;
                }

                MessageEntity messageEntity = await CreateMessageEntity(messageContext);

                await _store.StoreMessage(messageEntity);

                var status = await UpdateState(messageEntity);

                _logger?.LogInformation("Resolver: Updated Endpoint EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageEntity.EndpointId, status, messageEntity.EventId, messageContext.MessageId, messageEntity.SessionId);

                // Fire state-change notification (provider-neutral). Webhook is no longer
                // the only way for the WebApp to learn about updates; this works for any
                // storage provider including SQL Server which has no Change Feed.
                try { await _notifier.NotifyEndpointStateChangedAsync(messageEntity.EndpointId, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: state-change notification failed (non-fatal)"); }

                await messageContext.Complete(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown is not a resolver failure. Leave the message unsettled
                // so the transport can stop cooperatively and redeliver it later.
                throw;
            }
            catch (StorageProviderTransientException ex)
            {
                await HandleThrottling(messageContext, ex.RetryAfter, cancellationToken);
            }
            catch (TransientException transientException)
            {
                _logger?.LogError(transientException, "Resolver: Transient exception EventId:{EventId}", messageContext.EventId);
                await messageContext.Abandon(transientException);
            }
            catch (Exception unexpectedException)
            {
                _logger?.LogError(unexpectedException, "Resolver: Failed to handle message, add to DeadLetter. EventId:{EventId}", messageContext.EventId);
                await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);
            }
        }

        private async Task HandleThrottling(IMessageContext messageContext, TimeSpan? retryAfter, CancellationToken cancellationToken)
        {
            var retryCount = messageContext.ThrottleRetryCount;

            if (retryCount >= MaxThrottleRetries)
            {
                _logger?.LogError("Resolver: Max throttle retries ({MaxRetries}) exceeded. DeadLettering. EventId:{EventId}, SessionId:{SessionId}",
                    MaxThrottleRetries, messageContext.EventId, messageContext.SessionId);
                await messageContext.DeadLetter("Max throttle retries exceeded", null, cancellationToken);
                return;
            }

            // Calculate exponential backoff: 5s, 10s, 20s, 40s, ... up to 300s
            var calculatedDelay = TimeSpan.FromSeconds(
                Math.Min(BaseDelaySeconds * Math.Pow(2, retryCount), MaxDelaySeconds));

            // Honor a provider hint only when it is longer than the calculated
            // backoff. Providers such as SQL Server may not supply one.
            var providerRetryAfter = retryAfter.GetValueOrDefault();
            var useProviderRetryAfter = retryAfter.HasValue && providerRetryAfter > calculatedDelay;
            var delay = useProviderRetryAfter ? providerRetryAfter : calculatedDelay;

            _logger?.LogTrace(
                "Resolver: Transient storage delay decision - using {DelaySource}. ProviderRetryAfter:{ProviderRetryAfter}s, CalculatedBackoff:{CalculatedBackoff}s, EventId:{EventId}",
                useProviderRetryAfter ? "ProviderRetryAfter" : "CalculatedBackoff",
                retryAfter?.TotalSeconds,
                calculatedDelay.TotalSeconds,
                messageContext.EventId);

            _logger?.LogInformation(
                "Resolver: Storage provider temporarily unavailable. Scheduling redelivery in {DelaySeconds}s. EventId:{EventId}, SessionId:{SessionId}, RetryCount:{RetryCount}/{MaxRetries}",
                delay.TotalSeconds, messageContext.EventId, messageContext.SessionId, retryCount + 1, MaxThrottleRetries);

            try
            {
                await messageContext.ScheduleRedelivery(delay, retryCount + 1, cancellationToken);
            }
            catch (TransientException ex)
            {
                _logger?.LogInformation(ex, "Resolver: Failed to schedule redelivery. Abandoning for retry. EventId:{EventId}, SessionId:{SessionId}",
                    messageContext.EventId, messageContext.SessionId);
                await messageContext.Abandon(ex);
            }
        }

        /// <summary>
        /// Routes one heartbeat message. Endpoint answers update the heartbeat store; the
        /// Resolver's own liveness probe settles itself; the copies of endpoint requests
        /// that the Resolver's subscription also receives are dropped.
        /// </summary>
        /// <remarks>
        /// Heartbeat traffic is never dead-lettered — a monitoring probe must not be able
        /// to fill an operator's dead-letter queue. Transient storage failures leave the
        /// message unsettled so the session redelivers it; everything else completes.
        /// </remarks>
        private async Task HandleHeartbeatMessage(IMessageContext messageContext, CancellationToken cancellationToken)
        {
            if (messageContext.MessageType == MessageType.EventRequest)
            {
                // A heartbeat EventRequest addressed to the Resolver itself is the
                // WebApp's liveness probe; every other one is the copy of an
                // endpoint request the Resolver subscription also receives.
                if (Constants.ResolverId.Equals(messageContext.To, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSelfProbe(messageContext, cancellationToken);
                    return;
                }

                _logger?.LogTrace("Resolver: Dropped Heartbeat request copy. EventId:{EventId}, MessageId:{MessageId}",
                    messageContext.EventId, messageContext.MessageId);
                await messageContext.Complete(cancellationToken);
                return;
            }

            var endpointId = GetHeartbeatEndpointId(messageContext);
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                _logger?.LogWarning("Resolver: Heartbeat response without endpoint. EventId:{EventId}, MessageId:{MessageId}",
                    messageContext.EventId, messageContext.MessageId);
                await messageContext.Complete(cancellationToken);
                return;
            }

            if (_metadataStore is null)
            {
                _logger?.LogWarning("Resolver: heartbeat store not configured; completing without recording. EndpointId:{EndpointId}, EventId:{EventId}",
                    endpointId, messageContext.EventId);
                await messageContext.Complete(cancellationToken);
                return;
            }

            var heartbeat = CreateHeartbeat(messageContext);

            try
            {
                await _metadataStore.SetHeartbeat(heartbeat, endpointId);
            }
            catch (StorageProviderTransientException ex)
            {
                // Return without settling: the session redelivers the message. Heartbeats
                // deliberately skip the scheduled-redelivery path used for event traffic —
                // the next probe supersedes this one anyway.
                _logger?.LogInformation(ex, "Resolver: Storage temporarily unavailable, will reprocess heartbeat. EndpointId:{EndpointId}, EventId:{EventId}",
                    endpointId, messageContext.EventId);
                return;
            }

            _logger?.LogInformation("Resolver: Updated Heartbeat EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}",
                endpointId, heartbeat.EndpointHeartbeatStatus, messageContext.EventId, messageContext.MessageId);

            try { await _notifier.NotifyHeartbeatChangedAsync(endpointId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: heartbeat notification failed (non-fatal)"); }

            await messageContext.Complete(cancellationToken);
        }

        /// <summary>
        /// Answers the WebApp's Resolver liveness probe. Reaching this point already
        /// proves what the probe asks: the host is up, it is draining its Service Bus
        /// session subscription, and — once the write below succeeds — it can reach
        /// the message store. So the probe settles itself here rather than sending a
        /// response back over the bus.
        /// </summary>
        private async Task HandleSelfProbe(IMessageContext messageContext, CancellationToken cancellationToken)
        {
            if (_serviceHealthStore is null)
            {
                _logger?.LogWarning("Resolver: service health store not configured; completing liveness probe without recording. MessageId:{MessageId}",
                    messageContext.MessageId);
                await messageContext.Complete(cancellationToken);
                return;
            }

            var content = DeserializeHeartbeat(messageContext);
            var now = DateTime.UtcNow;
            var sentAt = TimestampOrDefault(content.ForwardSendTime, messageContext.EnqueuedTimeUtc);

            var health = new ServiceHealth
            {
                ServiceId = Constants.ResolverId,
                Status = HeartbeatStatus.On,
                Version = GetResolverVersion(),
                LastSeenUtc = now,
                RoundTripMs = sentAt == default ? null : (long?)Math.Max(0, (now - sentAt).TotalMilliseconds),
            };

            try
            {
                await _serviceHealthStore.SetServiceHealth(health);
            }
            catch (StorageProviderTransientException ex)
            {
                _logger?.LogInformation(ex, "Resolver: Storage temporarily unavailable, will reprocess liveness probe. EventId:{EventId}",
                    messageContext.EventId);
                return;
            }

            _logger?.LogInformation("Resolver: Answered liveness probe. RoundTripMs:{RoundTripMs}, Version:{Version}, MessageId:{MessageId}",
                health.RoundTripMs, health.Version, messageContext.MessageId);

            try { await _notifier.NotifyServiceHealthChangedAsync(Constants.ResolverId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: service health notification failed (non-fatal)"); }

            await messageContext.Complete(cancellationToken);
        }

        private static string GetResolverVersion()
        {
            var assembly = typeof(ResolverService).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the '+<sha>' source-revision suffix the .NET SDK appends to the
                // informational version; the release identity is the bare package version.
                return informational.Split('+')[0];
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        private static bool IsHeartbeat(IReceivedMessage message)
        {
            var eventTypeId = message.EventTypeId
                ?? message.MessageContent?.EventContent?.EventTypeId;
            return eventTypeId?.Equals(CoreHeartbeat.EventTypeId, StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// Attributes a heartbeat answer to an endpoint. The endpoint the payload names wins
        /// over the message's <c>From</c> — <c>ResponseService.CreateResponse</c> is static
        /// and does not stamp <c>From</c>, so a response from an older or hand-rolled
        /// emitter can arrive with it blank while the payload always carries the endpoint
        /// the SDK answered for.
        /// </summary>
        private static string GetHeartbeatEndpointId(IReceivedMessage message)
        {
            var content = DeserializeHeartbeat(message);
            return !string.IsNullOrWhiteSpace(content.Endpoint)
                ? content.Endpoint
                : message.From;
        }

        private static Heartbeat CreateHeartbeat(IReceivedMessage message)
        {
            var content = DeserializeHeartbeat(message);
            var now = DateTime.UtcNow;
            return new Heartbeat
            {
                MessageId = !string.IsNullOrWhiteSpace(message.CorrelationId)
                    ? message.CorrelationId
                    : message.MessageId,
                StartTime = TimestampOrDefault(content.ForwardSendTime, TimestampOrDefault(message.EnqueuedTimeUtc, now)),
                ReceivedTime = TimestampOrDefault(content.ForwardReceivedTime, now),
                EndTime = now,
                SdkVersion = content.SdkVersion ?? string.Empty,
                EndpointHeartbeatStatus = message.MessageType switch
                {
                    MessageType.ResolutionResponse => HeartbeatStatus.On,
                    MessageType.UnsupportedResponse => HeartbeatStatus.Unsupported,
                    MessageType.ErrorResponse => HeartbeatStatus.Off,
                    MessageType.DeferralResponse => HeartbeatStatus.Off,
                    _ => HeartbeatStatus.Unknown,
                },
            };
        }

        private static CoreHeartbeat DeserializeHeartbeat(IReceivedMessage message)
        {
            var json = message.MessageContent?.EventContent?.EventJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CoreHeartbeat();
            }

            try
            {
                return JsonConvert.DeserializeObject<CoreHeartbeat>(json) ?? new CoreHeartbeat();
            }
            catch (JsonException)
            {
                // A malformed probe payload still proves the endpoint answered; fall back
                // to the empty shape so attribution and timings degrade rather than fail.
                return new CoreHeartbeat();
            }
        }

        private static DateTime TimestampOrDefault(DateTime value, DateTime fallback) =>
            value == default ? fallback : value;

        private async Task<MessageEntity> CreateMessageEntity(IReceivedMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.MessageType == MessageType.RetryRequest)
            {
                var messageAudit = new MessageAuditEntity() { AuditorName = Constants.ManagerId, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Retry };
                await InstrumentAuditWrite(message, messageAudit);
            }

            var (endpointId, endpointRole) = DetermineEndpoint(message);

            return new MessageEntity
            {
                EventId = message.EventId,
                MessageId = message.MessageId,
                OriginatingMessageId = message.OriginatingMessageId,
                ParentMessageId = message.ParentMessageId,
                From = message.From,
                To = message.To,
                OriginatingFrom = message.OriginatingFrom,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,
                EnqueuedTimeUtc = message.EnqueuedTimeUtc,
                MessageContent = message.MessageContent,
                MessageType = message.MessageType,
                EndpointId = endpointId,
                EndpointRole = endpointRole,
                DeadLetterErrorDescription = message.DeadLetterErrorDescription,
                DeadLetterReason = message.DeadLetterReason,
                EventTypeId = message.EventTypeId ?? message?.MessageContent?.EventContent?.EventTypeId,
                // Per-message timings carried on the response message by the
                // subscriber. Null on EventRequest / original publishes.
                QueueTimeMs = message.QueueTimeMs,
                ProcessingTimeMs = message.ProcessingTimeMs,
                // PendingHandoff metadata. Sub-status is set only on the
                // PendingHandoffResponse audit row (the original Pending+Handoff
                // entry); the Manager-issued HandoffCompleted/HandoffFailed
                // requests are recorded as plain Pending so the subsequent
                // ResolutionResponse / ErrorResponse can flip the original.
                HandoffReason = message.HandoffReason,
                ExternalJobId = message.ExternalJobId,
                ExpectedBy = message.ExpectedBy,
                PendingSubStatus = message.MessageType == MessageType.PendingHandoffResponse ? "Handoff" : null,
                // CloudEvents identity carried on the response from a CloudEvents-consuming
                // subscriber; null for native messages.
                CloudEventId = message.CloudEventId,
                CloudEventSource = message.CloudEventSource,
                CloudEventType = message.CloudEventType,
                CloudEventSubject = message.CloudEventSubject,
            };
        }

        /// <summary>
        /// Determines the endpoint ID and role based on message properties.
        /// </summary>
        /// <remarks>
        /// Endpoint determination rules:
        /// 1. If message is from Broker with ErrorResponse type → Publisher role, use OriginatingFrom as endpoint
        /// 2. If message is from Broker → use To as endpoint (Subscriber role)
        /// 3. If message is a request type (EventRequest, ContinuationRequest, RetryRequest, ResubmissionRequest, SkipRequest) → use To as endpoint
        /// 4. Otherwise → use From as endpoint (response from subscriber)
        /// </remarks>
        internal (string endpointId, EndpointRole role) DetermineEndpoint(IReceivedMessage message)
        {
            var endpointRole = EndpointRole.Subscriber;
            string endpointId;

            // Request types are directed to the subscriber (use To)
            if (message.MessageType == MessageType.EventRequest ||
                     message.MessageType == MessageType.ContinuationRequest ||
                     message.MessageType == MessageType.RetryRequest ||
                     message.MessageType == MessageType.ResubmissionRequest ||
                     message.MessageType == MessageType.SkipRequest ||
                     message.MessageType == MessageType.HandoffCompletedRequest ||
                     message.MessageType == MessageType.HandoffFailedRequest)
            {
                endpointId = message.To;
            }
            // Response types come from the subscriber (use From)
            else
            {
                endpointId = message.From;
            }

            return (endpointId, endpointRole);
        }

        private UnresolvedEvent CreateUnresolvedEvent(MessageEntity message, long? processingTimeMsOverride = null)
        {
            return new UnresolvedEvent
            {
                UpdatedAt = DateTime.UtcNow,
                EnqueuedTimeUtc = message.EnqueuedTimeUtc,

                EventId = message.EventId,
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,

                ResolutionStatus = GetResultingStatus(message),
                EndpointRole = message.EndpointRole,
                EndpointId = message.EndpointId,
                RetryCount = message.RetryCount,
                RetryLimit = message.RetryLimit,
                MessageType = message.MessageType,
                DeadLetterReason = message.DeadLetterReason,
                DeadLetterErrorDescription = message.DeadLetterErrorDescription,

                LastMessageId = message.MessageId,
                OriginatingMessageId = message.OriginatingMessageId,
                ParentMessageId = message.ParentMessageId,
                Reason = message.MessageType == MessageType.SkipResponse
                    ? message.MessageContent?.ErrorContent?.ErrorText
                    : message.DeadLetterErrorDescription,
                OriginatingFrom = message.OriginatingFrom,

                EventTypeId = message.EventTypeId,
                To = message.To,
                From = message.From,
                MessageContent = message.MessageContent,
                QueueTimeMs = message.QueueTimeMs,
                // For terminal settlement of an event that went through async
                // handoff, override the per-hop handler duration with the
                // wall-clock span from the original EventRequest. The raw value
                // remains on the per-message MessageEntity row for auditing.
                ProcessingTimeMs = processingTimeMsOverride ?? message.ProcessingTimeMs,
                // PendingHandoff metadata: copy through from the projected
                // MessageEntity so the UnresolvedEvent (i.e. the audit row
                // surfaced by the WebApp) carries them too.
                PendingSubStatus = message.PendingSubStatus,
                HandoffReason = message.HandoffReason,
                ExternalJobId = message.ExternalJobId,
                ExpectedBy = message.ExpectedBy,
                // Surface CloudEvents identity on the tracking record (null for native).
                CloudEventId = message.CloudEventId,
                CloudEventSource = message.CloudEventSource,
                CloudEventType = message.CloudEventType,
                CloudEventSubject = message.CloudEventSubject,
            };
        }

        // Returns the wall-clock duration since the original EventRequest's
        // EnqueuedTimeUtc when settling a terminal response (Resolution/Error)
        // for an event that previously emitted a PendingHandoffResponse.
        // Returns null otherwise so the regular per-hop ProcessingTimeMs wins.
        private async Task<long?> ComputeHandoffWallClockMsIfTerminal(MessageEntity message)
        {
            if (message.MessageType != MessageType.ResolutionResponse &&
                message.MessageType != MessageType.ErrorResponse)
            {
                return null;
            }

            var history = (await _store.GetEventHistory(message.EventId)).ToList();
            if (!history.Any(m => m.MessageType == MessageType.PendingHandoffResponse))
            {
                return null;
            }

            var eventRequest = history.FirstOrDefault(m => m.MessageType == MessageType.EventRequest);
            if (eventRequest is null)
            {
                return null;
            }

            return (long)Math.Max(0, (DateTime.UtcNow - eventRequest.EnqueuedTimeUtc).TotalMilliseconds);
        }

        private async Task<ResolutionStatus> UpdateState(MessageEntity message)
        {
            ResolutionStatus status = GetResultingStatus(message);
            long? wallClockMs = await ComputeHandoffWallClockMsIfTerminal(message);
            UnresolvedEvent unresolvedEvent = CreateUnresolvedEvent(message, wallClockMs);

            var statusHandlers = new Dictionary<ResolutionStatus, Func<Task>>
            {
                [ResolutionStatus.Completed] = () => _store.UploadCompletedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Skipped] = () => _store.UploadSkippedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Failed] = () => _store.UploadFailedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Deferred] = () => _store.UploadDeferredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Pending] = () => _store.UploadPendingMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.DeadLettered] = () => _store.UploadDeadletteredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
                [ResolutionStatus.Unsupported] = () => _store.UploadUnsupportedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            };

            if (statusHandlers.TryGetValue(status, out var handler))
            {
                await InstrumentOutcomeWrite(message.EndpointId, status, handler);
            }

            return status;
        }

        private async Task InstrumentAuditWrite(IReceivedMessage message, MessageAuditEntity audit)
        {
            var auditType = audit.AuditType.ToString().ToLowerInvariant();
            // RetryRequest is a request type, so DetermineEndpoint resolves to message.To.
            // We use that directly to avoid recomputing.
            var endpoint = message.To;
            var startTimestamp = Stopwatch.GetTimestamp();
            using var activity = NimBusActivitySources.Resolver.StartActivity(
                "NimBus.Resolver.RecordAudit", ActivityKind.Internal);
            if (activity is not null)
            {
                if (!string.IsNullOrEmpty(endpoint))
                    activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
                activity.SetTag(MessagingAttributes.NimBusAuditType, auditType);
            }

            string? errorType = null;
            try
            {
                await _store.StoreMessageAudit(message.EventId, audit);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                errorType = ex.GetType().FullName;
                if (activity is not null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.SetTag(MessagingAttributes.ErrorType, errorType);
                }
                throw;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = BuildAuditTags(endpoint, auditType, errorType);
                NimBusMeters.ResolverWriteDuration.Record(elapsed, tags);
                NimBusMeters.ResolverAuditWritten.Add(1, tags);
            }
        }

        private async Task InstrumentOutcomeWrite(string endpointId, ResolutionStatus status, Func<Task> handler)
        {
            var outcome = status.ToString().ToLowerInvariant();
            var startTimestamp = Stopwatch.GetTimestamp();
            using var activity = NimBusActivitySources.Resolver.StartActivity(
                "NimBus.Resolver.RecordOutcome", ActivityKind.Internal);
            if (activity is not null)
            {
                if (!string.IsNullOrEmpty(endpointId))
                    activity.SetTag(MessagingAttributes.NimBusEndpoint, endpointId);
                activity.SetTag(MessagingAttributes.NimBusOutcome, outcome);
            }

            string? errorType = null;
            try
            {
                await handler();
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                errorType = ex.GetType().FullName;
                if (activity is not null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.SetTag(MessagingAttributes.ErrorType, errorType);
                }
                throw;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = BuildOutcomeTags(endpointId, outcome, errorType);
                NimBusMeters.ResolverWriteDuration.Record(elapsed, tags);
                NimBusMeters.ResolverOutcomeWritten.Add(1, tags);
            }
        }

        private static KeyValuePair<string, object?>[] BuildOutcomeTags(string? endpoint, string outcome, string? errorType)
        {
            var tags = new List<KeyValuePair<string, object?>>(3)
            {
                new(MessagingAttributes.NimBusOutcome, outcome),
            };
            if (!string.IsNullOrEmpty(endpoint))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
            if (!string.IsNullOrEmpty(errorType))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
            return tags.ToArray();
        }

        private static KeyValuePair<string, object?>[] BuildAuditTags(string? endpoint, string auditType, string? errorType)
        {
            var tags = new List<KeyValuePair<string, object?>>(3)
            {
                new(MessagingAttributes.NimBusAuditType, auditType),
            };
            if (!string.IsNullOrEmpty(endpoint))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
            if (!string.IsNullOrEmpty(errorType))
                tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
            return tags.ToArray();
        }

        private ResolutionStatus GetResultingStatus(MessageEntity message)
        {
            if (message.DeadLetterErrorDescription != null)
            {
                return ResolutionStatus.DeadLettered;
            }

            if (MessageTypeToStatusMap.TryGetValue(message.MessageType, out var status))
            {
                return status;
            }

            throw new ArgumentException($"Unexpected {nameof(MessageType)}", nameof(message.MessageType));
        }

    }
}
