using Azure.Messaging.ServiceBus;
using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract
{
    public class EventImplementation : IEventApiController
    {
        // Payload-carrying request message types — the ones that actually carry
        // the original event JSON to be re-delivered on resubmit. Handoff control
        // requests (HandoffCompleted/FailedRequest) and SkipRequest carry no
        // payload; terminal *Response messages may carry a stale/empty payload
        // (notably a failed hand-off's ErrorResponse). Mirrored by the frontend's
        // PAYLOAD_REQUEST_TYPES in message-listing.tsx.
        private static readonly Core.Messages.MessageType[] PayloadCarryingRequestTypes =
        {
            Core.Messages.MessageType.EventRequest,
            Core.Messages.MessageType.ResubmissionRequest,
            Core.Messages.MessageType.RetryRequest,
            Core.Messages.MessageType.ContinuationRequest,
            Core.Messages.MessageType.ProcessDeferredRequest,
        };

        private readonly IPlatform platform;
        private readonly ILogger<EventImplementation> logger;
        private readonly INimBusMessageStore cosmosClient;
        private readonly IManagerClient managerClient;
        private readonly IApplicationInsightsService applicationInsightsService;
        private readonly IEndpointAuthorizationService authorizationService;
        private readonly IAdminService adminService;
        private readonly ServiceBusClient serviceBusClient;
        private readonly IAuditLogService auditLogService;
        private readonly IHandoffSettlementService handoffSettlement;
        private readonly IHttpContextAccessor httpContextAccessor;

        public EventImplementation(
            IApplicationInsightsService applicationInsightsService,
            IPlatform platform,
            IManagerClient managerClient,
            ILogger<EventImplementation> logger,
            INimBusMessageStore cosmosClient,
            IEndpointAuthorizationService authorizationService,
            IAdminService adminService,
            ServiceBusClient serviceBusClient,
            IAuditLogService auditLogService,
            IHandoffSettlementService handoffSettlement,
            IHttpContextAccessor httpContextAccessor)
        {
            this.platform = platform;
            this.logger = logger;
            this.cosmosClient = cosmosClient;
            this.managerClient = managerClient;
            this.applicationInsightsService = applicationInsightsService;
            this.authorizationService = authorizationService;
            this.adminService = adminService;
            this.serviceBusClient = serviceBusClient;
            this.auditLogService = auditLogService;
            this.handoffSettlement = handoffSettlement;
            this.httpContextAccessor = httpContextAccessor;
        }
        public async Task<ActionResult<Message>> GetEventIdsAsync(string eventId, string messageId)
        {
            try
            {
                var messageEntity = await cosmosClient.GetMessage(eventId, messageId);
                if (messageEntity != null)
                {
                    var message = Mapper.MessageFromMessageEntity(messageEntity);
                    return message;
                }
                return new NotFoundObjectResult("Event Message not found");
            }
            catch (Exception e)
            {
                logger.LogWarning("Event Message not found. EventId: {EventId}, MessageId: {MessageId}, Ex: {Exception}", eventId, messageId, e.Message);
                return new NotFoundObjectResult("Event Message not found");
            }
        }

        public async Task<ActionResult<Event>> GetUnresolvedFailedEventIdAsync(string endpointId, string eventId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var unresolvedEvent = await cosmosClient.GetFailedEvent(endpointId, eventId, sessionId);
                return Mapper.EventFromMessageStoreEvent(unresolvedEvent);
            }
            catch (Exception e)
            {
                logger.LogWarning("Unresolved failed not found. EndpointId: {EndpointId}, EventId: {EventId}, SessionId: {SessionId}, Ex: {Exception}", endpointId, eventId, sessionId, e.Message);
                return new NotFoundObjectResult("Unresolved failed not found");
            }
        }

        public async Task<ActionResult<IEnumerable<MessageAudit>>> GetMessageAuditsEventIdAsync(string eventId)
        {
            var audits = await cosmosClient.GetMessageAudits(eventId);
            if (audits != null)
            {
                return audits.Reverse().Select(Mapper.MessageAuditFromMessageAuditEntity).ToList();
            }

            return new NotFoundResult();
        }

        public async Task<IActionResult> PostMessageAuditAsync(MessageAudit body, string eventId)
        {
            try
            {
                var audit = Mapper.MessageAuditEntityFromMessageAudit(body);
                await cosmosClient.StoreMessageAudit(eventId, audit);
                return new OkResult();
            }
            catch (Exception e)
            {
                logger.LogWarning("Failed to PostMessageAudit: {ExceptionMessage}", e.Message);
                return new BadRequestResult();
            }
        }

        public async Task<IActionResult> PostResubmitEventIdsAsync(string eventId, string messageId)
        {
            logger.LogInformation("Resubmit message. EventId:{EventId}, MessageId:{MessageId}", eventId, messageId);

            string eventTypeId;
            string endpoint;
            MessageEntity errorResponse = await GetMessageWithFallback(eventId, messageId);
            if (errorResponse == null)
            {
                logger.LogWarning("Could not resubmit message. Message not found. EventId: {EventId}, MessageId: {MessageId}", eventId, messageId);
                return new BadRequestResult();
            }

            // Resubmit must replay the original event payload. For a failed
            // hand-off the lastMessageId points at the terminal ErrorResponse,
            // whose MessageContent carries no usable event JSON — so source the
            // payload (and, when missing, the event type) from the latest REQUEST
            // message that actually carries it (the original EventRequest, or a
            // later Resubmission/Retry/Continuation/ProcessDeferredRequest). Falls
            // back to the resolved message so non-hand-off resubmits are
            // unchanged. Same source as the frontend's resubmit prefill and the
            // resubmit-with-changes event-type resolution below.
            var history = await cosmosClient.GetEventHistory(eventId);
            MessageEntity? latestRequest = LatestRequestMessageWithPayload(history);
            MessageEntity requestMessage = latestRequest ?? errorResponse;

            eventTypeId = errorResponse.EventTypeId;
            if (string.IsNullOrEmpty(eventTypeId))
            {
                MessageEntity typeSource = latestRequest
                    ?? await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId)
                    ?? errorResponse;
                eventTypeId = !string.IsNullOrWhiteSpace(typeSource.EventTypeId)
                    ? typeSource.EventTypeId
                    : typeSource.MessageContent?.EventContent?.EventTypeId!;
            }

            if (errorResponse.OriginatingMessageId.Equals("self", StringComparison.Ordinal))
            {
                endpoint = errorResponse.To;
            }
            else
            {
                endpoint = errorResponse.From;
            }

            var eventJson = requestMessage.MessageContent?.EventContent?.EventJson!;

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.Resubmit, httpContextAccessor.HttpContext,
                    accessDenied: true, eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
            }

            // Deliberately sequential — do not parallelize. ArchiveFailedEvent
            // soft-deletes the event (deleted=true + 30d TTL); if the publish
            // fails, the event must remain visible in the failed list. Running
            // these concurrently would archive events whose resubmit never left.
            await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, eventJson);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
            await auditLogService.LogAuditAsync(MessageAuditType.Resubmit, httpContextAccessor.HttpContext,
                eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            return new OkResult();
        }

        public async Task<IActionResult> PostSkipEventIdsAsync(string eventId, string messageId)
        {
            logger.LogInformation("Skip message. EventId:{EventId}, MessageId:{MessageId}", eventId, messageId);

            string eventTypeId;
            string endpoint;
            MessageEntity errorResponse = await GetMessageWithFallback(eventId, messageId);
            if (errorResponse == null)
            {
                logger.LogWarning("Could not skip message. Message not found. EventId: {EventId}, MessageId: {MessageId}", eventId, messageId);
                return new BadRequestResult();
            }

            eventTypeId = errorResponse.EventTypeId;
            if (string.IsNullOrEmpty(eventTypeId))
            {
                MessageEntity origMessage = await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId);
                eventTypeId = origMessage.EventTypeId;
            }

            if (errorResponse.OriginatingMessageId.Equals("self", StringComparison.Ordinal))
            {
                endpoint = errorResponse.To;
            }
            else
            {
                endpoint = errorResponse.From;
            }

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.Skip, httpContextAccessor.HttpContext,
                    accessDenied: true, eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
            }

            await managerClient.Skip(errorResponse, endpoint, eventTypeId);
            await auditLogService.LogAuditAsync(MessageAuditType.Skip, httpContextAccessor.HttpContext,
                eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);

            return new OkResult();
        }
        public Task<IActionResult> PostHandoffCompleteAsync(CompleteHandoffRequest body, string endpointId, string eventId, string messageId)
            => SettlePendingHandoffAsync(
                endpointId, eventId, messageId,
                MessageAuditType.CompleteHandoff,
                body?.Note,
                (pendingEntry, operatorName) =>
                {
                    // Carry the operator note (and who completed it) on the completion
                    // payload so the resulting Completed audit row records the manual
                    // intervention, not just an anonymous external job finishing.
                    var detailsJson = string.IsNullOrWhiteSpace(body?.Note)
                        ? null
                        : JsonConvert.SerializeObject(new { note = body.Note, completedBy = operatorName });
#pragma warning disable CS0618 // Manager-side settlement: the WebApp *is* the Manager. The
                    // [Obsolete] hint steers adapters toward IHandoffClient (endpoint-bound,
                    // registered per endpoint); the manager path takes the endpoint as a
                    // parameter and reuses the ServiceBusClient this controller already holds.
                    return managerClient.CompleteHandoff(pendingEntry, endpointId, detailsJson);
#pragma warning restore CS0618
                });

        public Task<IActionResult> PostHandoffFailAsync(FailHandoffRequest body, string endpointId, string eventId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(body?.Reason))
                return Task.FromResult<IActionResult>(new BadRequestObjectResult("A failure reason is required."));

            return SettlePendingHandoffAsync(
                endpointId, eventId, messageId,
                MessageAuditType.FailHandoff,
                body.Reason,
                (pendingEntry, _) =>
                {
#pragma warning disable CS0618 // See PostHandoffCompleteAsync — manager-side settlement.
                    return managerClient.FailHandoff(pendingEntry, endpointId, body.Reason, body.ErrorType);
#pragma warning restore CS0618
                });
        }

        // Operator entry to the two handoff-settlement actions. Does the operator-only
        // pre-checks (endpoint exists, caller manages it) and then delegates the load →
        // PendingHandoff guard → settle → audit core to the shared IHandoffSettlementService,
        // which the agent settle path also uses so neither can skip the audit row.
        private async Task<IActionResult> SettlePendingHandoffAsync(
            string endpointId,
            string eventId,
            string messageId,
            MessageAuditType auditType,
            string auditComment,
            Func<MessageEntity, string, Task> settle)
        {
            if (!EndpointVerificationService.EndpointExists(platform, endpointId))
                return new NotFoundObjectResult("Endpoint not found");

            if (!authorizationService.IsManagerOfEndpoint(endpointId))
            {
                await auditLogService.LogAuditAsync(auditType, httpContextAccessor.HttpContext,
                    accessDenied: true, eventId: eventId, endpointId: endpointId);
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpointId}'.");
            }

            var auditorName = AuditLogService.ResolveAuditorName(httpContextAccessor.HttpContext);
            return await handoffSettlement.SettleAsync(
                endpointId, eventId, messageId, auditType, auditComment, auditorName,
                httpContextAccessor.HttpContext, settle);
        }

        public async Task<ActionResult<DeferredReprocessResult>> PostReprocessDeferredAsync(string endpointId, string sessionId)
        {
            if (!EndpointVerificationService.EndpointExists(platform, endpointId))
                return new NotFoundObjectResult("Endpoint not found");

            if (!authorizationService.IsManagerOfEndpoint(endpointId))
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpointId}'.");

            var result = await adminService.ReprocessDeferredAsync(endpointId, sessionId);
            return new OkObjectResult(result);
        }

        public async Task<ActionResult<Event>> GetEventIdAsync(string id, string endpoint)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpoint);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var unresolvedEvent = await cosmosClient.GetEvent(endpoint, id);
                if (unresolvedEvent == null) return new BadRequestResult();
                var result = Mapper.EventFromMessageStoreEvent(unresolvedEvent);

                // The Payload shown on the detail page must be the inbound *event
                // request* — the original EventRequest, or the latest
                // ResubmissionRequest if the event was resubmitted — never a
                // ResolutionResponse (which carries the handler's result, e.g. an
                // imported record id) or a handoff control message. The endpoint
                // container stores the *last* message's content, which for
                // completed/settled events is the resolution response, so resolve
                // the request payload from the full message history instead.
                var requestJson = await GetLatestEventRequestPayload(id);
                if (!string.IsNullOrEmpty(requestJson))
                {
                    result.MessageContent ??= new ManagementApi.MessageContent();
                    result.MessageContent.EventContent ??= new ManagementApi.EventContent();
                    result.MessageContent.EventContent.EventJson = requestJson;
                }

                return result;
            }
            catch (Exception e)
            {
                logger.LogWarning("Event not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpoint, id, e.Message);
                return new NotFoundObjectResult("Event not found");
            }
        }

        public async Task<ActionResult<EventDetails>> GetEventDetailsIdAsync(string id, string endpoint)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpoint);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.GetEventDetails, httpContextAccessor.HttpContext,
                    accessDenied: true, eventId: id, endpointId: endpoint);
                return new ForbidResult();
            }

            await auditLogService.LogAuditAsync(MessageAuditType.GetEventDetails, httpContextAccessor.HttpContext,
                eventId: id, endpointId: endpoint);

            var eventDetails = new EventDetails();

            try
            {
                var failedMessage = await cosmosClient.GetFailedMessage(id, endpoint);
                if (failedMessage != null)
                {
                    logger.LogInformation("Failed message found. EventId: {EventId}, MessageId: {MessageId}, Endpoint: {Endpoint}, MessageType: {MessageType}", failedMessage.EventId, failedMessage.MessageId, endpoint, failedMessage.MessageType);
                    eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(failedMessage);

                    var downloadedMsg = await cosmosClient.GetMessage(eventDetails.FailedMessage.EventId,
                        eventDetails.FailedMessage.OriginatingMessageId);
                    if (downloadedMsg != null)
                    {
                        eventDetails.OriginatingMessage = Mapper.MessageFromMessageEntity(downloadedMsg);
                    }

                    return eventDetails;
                }

                var deadletteredMessage = await cosmosClient.GetDeadletteredMessage(id, endpoint);


                if (deadletteredMessage != null)
                {
                    logger.LogInformation("Message found in deadletter. EventId: {EventId}, MessageId: {MessageId}, Endpoint: {Endpoint}, MessageType: {MessageType}", deadletteredMessage.EventId, deadletteredMessage.MessageId, endpoint, deadletteredMessage.MessageType);
                    if (deadletteredMessage.MessageType == Core.Messages.MessageType.ResolutionResponse)
                    {
                        var completedMsg = await cosmosClient.GetMessage(deadletteredMessage.EventId, deadletteredMessage.OriginatingMessageId);
                        if (completedMsg != null)
                        {
                            eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(completedMsg);
                        }
                        return eventDetails;
                    }

                    eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(deadletteredMessage);

                    eventDetails.FailedMessage.ErrorContent = Mapper.MessageErrorContentFromErroryContent(deadletteredMessage);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning("GetEventDetailsIdAsync: {Exception}", e.Message);
            }

            return eventDetails;
        }

        public async Task<ActionResult<IEnumerable<EventLogEntry>>> GetEventDetailsLogsIdAsync(string id, string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var logs = new List<EventLogEntry>();
            try
            {
                logs = (await applicationInsightsService.GetLogs(id))
                    .Where(l => l.To.Equals(endpointId, StringComparison.OrdinalIgnoreCase) || l.From.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
                    .Select(Mapper.EventLogEntryFromLogEntry)
                    .ToList();
            }
            catch (Exception e)
            {
                logger.LogWarning("GetEventDetailsLogsIdAsync: {Exception}", e.Message);
            }
            return logs;
        }

        public async Task<ActionResult<IEnumerable<Message>>> GetEventDetailsHistoryIdAsync(string id, string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var histories = new List<Message>();
            try
            {
                histories = (await cosmosClient.GetEventHistory(id))
                    .Where(x => x.EndpointId.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.EnqueuedTimeUtc)
                    .Select(Mapper.MessageFromMessageEntity)
                    .ToList();
            }
            catch (Exception e)
            {
                logger.LogWarning("GetEventDetailsHistoryIdAsync: {Exception}", e.Message);
            }
            return histories;
        }

        public async Task<IActionResult> PostComposeNewEventAsync(ResubmitWithChanges body)
        {
            logger.LogInformation("Compose new event. EventTypeId:{EventTypeId}", body?.EventTypeId);

            var eventType = platform.EventTypes.FirstOrDefault(x => x.Id.Equals(body.EventTypeId, StringComparison.OrdinalIgnoreCase));
            if (eventType == null)
            {
                logger.LogError("Could not find event type: {EventTypeId}", body.EventTypeId);
                return new BadRequestObjectResult("Could not find event type: " + body.EventTypeId);
            }

            var producingEndpoint = platform.GetProducers(eventType).FirstOrDefault();
            if (producingEndpoint == null)
            {
                logger.LogError("Could not find any producers for the given event: {EventTypeId}", body.EventTypeId);
                return new BadRequestObjectResult("Could not find any producers for the given event");
            }

            if (string.IsNullOrWhiteSpace(body.EventContent))
            {
                logger.LogError("Event content is empty");
                return new BadRequestObjectResult("Event content is empty");
            }

            if (!authorizationService.IsManagerOfEndpoint(producingEndpoint.Id))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.Compose, httpContextAccessor.HttpContext,
                    accessDenied: true, data: JsonConvert.SerializeObject(body),
                    endpointId: producingEndpoint.Id, eventTypeId: body.EventTypeId);
                throw new UnauthorizedAccessException($"User is unauthorized to compose events for endpoint '{producingEndpoint.Id}'.");
            }

            var type = eventType.GetEventClassType();
            try
            {
                var @event = (Core.Events.IEvent)JsonConvert.DeserializeObject(body.EventContent, type);
                var validationResult = @event.TryValidate();
                if (!validationResult.IsValid)
                {
                    string errorMessage = string.Join(", ", validationResult.ValidationResults.Select(x => x.ErrorMessage));
                    return new BadRequestObjectResult($"Validation failed. Event does not fulfill the scheme '{type.Name}' Error: '{errorMessage}'");
                }

                // Publish directly to the producing endpoint's topic via the SDK PublisherClient,
                // so the message envelope (sessionId, correlationId, EventRequest type, EventContent)
                // matches what every other producer in the platform sends.
                var publisher = await PublisherClient.CreateAsync(serviceBusClient, producingEndpoint.Id);
                await publisher.Publish(@event);
                await auditLogService.LogAuditAsync(MessageAuditType.Compose, httpContextAccessor.HttpContext,
                    data: JsonConvert.SerializeObject(body),
                    endpointId: producingEndpoint.Id, eventTypeId: body.EventTypeId);
                return new OkResult();
            }
            catch (JsonReaderException e)
            {
                logger.LogError("Could not parse the event content. Exception: {ExceptionMessage}", e.Message);
                return new BadRequestObjectResult($"Could not parse the value '{e.Path}'");
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not compose event.");
                return new BadRequestObjectResult($"Could not compose event: {e.Message}");
            }
        }

        public async Task<IActionResult> PostResubmitWithChangesEventIdsAsync(ResubmitWithChanges body, string eventId, string messageId)
        {
            logger.LogInformation("Resubmit message with changes. EventId:{EventId}, MessageId:{MessageId}, Body:{Body}", eventId, messageId, JsonConvert.SerializeObject(body));
            string endpoint;

            MessageEntity errorResponse = await GetMessageWithFallback(eventId, messageId);
            if (errorResponse == null)
            {
                logger.LogWarning("Could not resubmit message with changes. Message not found. EventId: {EventId}, MessageId: {MessageId}", eventId, messageId);
                return new BadRequestResult();
            }

            // If error response message is a result of forwarding a deadlettered message.
            if (errorResponse.OriginatingMessageId.Equals("self", StringComparison.Ordinal))
            {
                endpoint = errorResponse.To;
            }
            else
            {
                endpoint = errorResponse.From;
            }

            string eventTypeId = body.EventTypeId;
            if (string.IsNullOrEmpty(body.EventTypeId))
            {
                eventTypeId = errorResponse.EventTypeId;

                if (string.IsNullOrEmpty(eventTypeId))
                {
                    // Same source as the frontend's resubmit prefill: the latest
                    // request message that carries the event payload (the original
                    // EventRequest, or a later resubmission/retry). For a failed
                    // hand-off the terminal ErrorResponse carries no event type,
                    // so resolve it from the request history rather than the
                    // originating message. Falls back to the originating-message
                    // lookup when no request message carries a payload, and
                    // finally to the terminal message itself.
                    var history = await cosmosClient.GetEventHistory(eventId);
                    MessageEntity requestMessage = LatestRequestMessageWithPayload(history)
                        ?? await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId)
                        ?? errorResponse;
                    eventTypeId = !string.IsNullOrWhiteSpace(requestMessage.EventTypeId)
                        ? requestMessage.EventTypeId
                        : requestMessage.MessageContent?.EventContent?.EventTypeId!;
                }
            }

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
                    accessDenied: true, data: JsonConvert.SerializeObject(body),
                    eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
            }

            // Deliberately sequential — do not parallelize. ArchiveFailedEvent
            // soft-deletes the event (deleted=true + 30d TTL); if the publish
            // fails, the event must remain visible in the failed list.
            await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, body.EventContent);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
            await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
                data: JsonConvert.SerializeObject(body),
                eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);

            return new OkResult();
        }

        public async Task<ActionResult<BlockedEventsPage>> GetEventBlockedIdAsync(int skip, int take, string endpointId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            // Server-side bounds: skip is non-negative; take clamps to [1, 200] with a default of 50.
            // NSwag binds the route's optional query params with their schema defaults (skip=0, take=50);
            // the clamping below is belt-and-braces against external callers passing negatives or huge values.
            var safeSkip = skip < 0 ? 0 : skip;
            var safeTake = take <= 0 ? 50 : Math.Min(take, 200);

            try
            {
                var page = await cosmosClient.GetBlockedEventsOnSession(endpointId, sessionId, safeSkip, safeTake);

                return new BlockedEventsPage
                {
                    Items = page.Items.Select(Mapper.BlockedEventFromBlockedMessageEvent).ToList(),
                    Total = page.Total,
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }

        public async Task<ActionResult<IEnumerable<Event>>> GetEventPendingIdAsync(string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var events = (await cosmosClient.GetPendingEventsOnSession(endpointId))
                    .Select(Mapper.EventFromMessageStoreEvent)
                    .ToList();

                return events;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }

        public async Task<IActionResult> DeleteEventInvalidIdAsync(string endpointId, string eventId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var result = await cosmosClient.RemoveMessage(eventId, sessionId, endpointId);
                return result
                    ? new OkResult()
                    : new NotFoundObjectResult($"Event '{eventId}' not found or could not be deleted");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }


        public async Task<ActionResult<Event>> GetEventUnsupportedEndpointIdEventIdAsync(string endpointId, string eventId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var result = await cosmosClient.GetUnsupportedEvent(endpointId, eventId, sessionId);
                return Mapper.EventFromMessageStoreEvent(result);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }

        public async Task<ActionResult<Event>> GetEventDeadletterEndpointIdEventIdAsync(string endpointId, string eventId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var result = await cosmosClient.GetDeadletteredEvent(endpointId, eventId, sessionId);
                return Mapper.EventFromMessageStoreEvent(result);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }

        public async Task<ActionResult<SearchResponse>> PostApiEventEndpointIdGetByFilterAsync(SearchRequest body, string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            // Spec 008 FR-032: pass the search filter (serialized) as Data so
            // operators answering "what searches has user X run?" see the
            // query parameters, not just the bare action.
            var searchDataJson = JsonConvert.SerializeObject(body);

            if (!authorizationService.IsManagerOfEndpoint(endpointId))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, httpContextAccessor.HttpContext,
                    accessDenied: true, data: searchDataJson, endpointId: endpointId);
                return new ForbidResult();
            }

            try
            {
                var filter = Mapper.MapFilter(body.EventFilter);
                filter.EndPointId = endpointId;  // Use validated URL parameter instead of body value
                var reponse = await cosmosClient.GetEventsByFilter(filter, body.ContinuationToken, body.MaxSearchItemsCount);
                await auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, httpContextAccessor.HttpContext,
                    data: searchDataJson, endpointId: endpointId);
                var events = reponse.Events
                    .Select(Mapper.EventFromMessageStoreEvent)
                    .ToList();
                await AttachResubmitCounts(endpointId, events);
                return new SearchResponse
                {
                    Events = events,
                    ContinuationToken = reponse.ContinuationToken
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }
        // Fills each event's ResubmitCount from the audit log in a single batched
        // query, so the event list can show how many times an event was
        // resubmitted without a per-row round-trip. Fail-soft: the count is a
        // display nicety — an enrichment failure must not break search.
        private async Task AttachResubmitCounts(string endpointId, List<Event> events)
        {
            var eventIds = events
                .Select(e => e.EventId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();
            if (eventIds.Count == 0) return;

            try
            {
                var counts = await cosmosClient.GetResubmitCounts(endpointId, eventIds);
                foreach (var ev in events)
                {
                    if (ev.EventId != null && counts.TryGetValue(ev.EventId, out var count))
                        ev.ResubmitCount = count;
                }
            }
            catch (Exception e)
            {
                logger.LogWarning("AttachResubmitCounts failed for endpoint {EndpointId}: {Exception}", endpointId, e.Message);
            }
        }

        // The detail-page Payload should reflect the event request that was
        // processed, not whatever the last message on the event happened to carry
        // (a ResolutionResponse, handoff control message, etc.). Returns the
        // EventJson of the most recent EventRequest / ResubmissionRequest in the
        // event's history, or null when none carries event content.
        private async Task<string> GetLatestEventRequestPayload(string eventId)
        {
            try
            {
                // Server-side TOP 1 (single-partition on the messages container)
                // instead of pulling the whole message history and filtering in
                // memory on every event-detail load.
                var request = await cosmosClient.GetLatestEventRequestMessage(eventId);
                return request?.MessageContent?.EventContent?.EventJson;
            }
            catch (Exception e)
            {
                logger.LogWarning("GetLatestEventRequestPayload failed for EventId {EventId}: {Exception}", eventId, e.Message);
                return null;
            }
        }

        // The latest request message that still carries the event payload — the
        // "latest request message sent". Lets resubmit-with-changes resolve the
        // event type from the original EventRequest (or a later Resubmission/
        // Retry/Continuation/ProcessDeferredRequest) rather than the terminal
        // ErrorResponse, which for a failed hand-off carries neither payload nor
        // event type. Internal for unit tests (InternalsVisibleTo).
        internal static MessageEntity? LatestRequestMessageWithPayload(IEnumerable<MessageEntity> history) =>
            history
                .Where(m => PayloadCarryingRequestTypes.Contains(m.MessageType)
                         && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
                .OrderByDescending(m => m.EnqueuedTimeUtc)
                .FirstOrDefault();

        private async Task<MessageEntity> GetMessageWithFallback(string eventId, string messageId)
        {
            var message = await cosmosClient.GetMessage(eventId, messageId);
            if (message != null) return message;

            // Fallback: the message wasn't in the shared messages container, so probe
            // the per-endpoint containers. An event lives in exactly one of them, so
            // probe concurrently rather than serially — the old loop cost one
            // cross-partition query per endpoint in sequence (10-20s on large
            // topologies) on what is a rare error/recovery path.
            var probes = platform.Endpoints.Select(async ep =>
            {
                try
                {
                    return (ep.Id, Event: await cosmosClient.GetEvent(ep.Id, eventId));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Fallback lookup failed for endpoint {EndpointId}", ep.Id);
                    return (ep.Id, Event: (UnresolvedEvent)null);
                }
            });

            var hit = Array.Find(await Task.WhenAll(probes), r => r.Event != null);
            if (hit.Event != null)
            {
                logger.LogInformation("Message {MessageId} not found in messages container, using fallback from endpoint {EndpointId}", messageId, hit.Id);
                return MessageEntityFromUnresolvedEvent(hit.Event);
            }

            return null;
        }

        private static MessageEntity MessageEntityFromUnresolvedEvent(UnresolvedEvent e)
        {
            return new MessageEntity
            {
                EventId = e.EventId,
                MessageId = e.LastMessageId,
                EventTypeId = e.EventTypeId,
                OriginatingMessageId = e.OriginatingMessageId,
                ParentMessageId = e.ParentMessageId,
                From = e.From,
                To = e.To,
                OriginatingFrom = e.OriginatingFrom,
                SessionId = e.SessionId,
                CorrelationId = e.CorrelationId,
                EnqueuedTimeUtc = e.EnqueuedTimeUtc,
                MessageContent = e.MessageContent,
                MessageType = e.MessageType,
                EndpointRole = e.EndpointRole,
                EndpointId = e.EndpointId,
                RetryCount = e.RetryCount,
                RetryLimit = e.RetryLimit,
                DeadLetterReason = e.DeadLetterReason,
                DeadLetterErrorDescription = e.DeadLetterErrorDescription,
                QueueTimeMs = e.QueueTimeMs,
                ProcessingTimeMs = e.ProcessingTimeMs,
            };
        }
    }
}
