using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract
{
    public class EventImplementation : IEventApiController
    {
        private readonly IPlatform platform;
        private readonly ILogger<EventImplementation> logger;
        private readonly ICosmosDbClient cosmosClient;
        private readonly IManagerClient managerClient;
        private readonly IApplicationInsightsService applicationInsightsService;
        private readonly IConfiguration configuration;
        private readonly IEndpointAuthorizationService authorizationService;

        public EventImplementation(
            IApplicationInsightsService applicationInsightsService,
            IPlatform platform,
            IManagerClient managerClient,
            ILogger<EventImplementation> logger,
            ICosmosDbClient cosmosClient,
            IConfiguration config,
            IEndpointAuthorizationService authorizationService)
        {
            this.platform = platform;
            this.logger = logger;
            this.cosmosClient = cosmosClient;
            this.managerClient = managerClient;
            this.applicationInsightsService = applicationInsightsService;
            configuration = config;
            this.authorizationService = authorizationService;
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

            var messageAuditEntity = authorizationService.GetMessageAuditEntity(MessageAuditType.Resubmit);
            var eventJson = errorResponse.MessageContent.EventContent.EventJson;

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");

            await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, eventJson);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
            await cosmosClient.StoreMessageAudit(eventId, messageAuditEntity);
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

            var messageAuditEntity = authorizationService.GetMessageAuditEntity(MessageAuditType.Skip);

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");

            await managerClient.Skip(errorResponse, endpoint, eventTypeId);
            await cosmosClient.StoreMessageAudit(eventId, messageAuditEntity);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);

            return new OkResult();
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

                // Completed events don't store eventContent in the endpoint container.
                // Fetch from the messages container if missing.
                if (unresolvedEvent.ResolutionStatus == MessageStore.ResolutionStatus.Completed
                    && string.IsNullOrEmpty(unresolvedEvent.MessageContent?.EventContent?.EventJson))
                {
                    var history = await cosmosClient.GetEventHistory(id);
                    var messageWithContent = history.FirstOrDefault(m =>
                        !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson));
                    if (messageWithContent != null)
                    {
                        result.MessageContent ??= new ManagementApi.MessageContent();
                        result.MessageContent.EventContent ??= new ManagementApi.EventContent();
                        result.MessageContent.EventContent.EventJson =
                            messageWithContent.MessageContent.EventContent.EventJson;
                    }
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
            logger.LogInformation("Compose new event. Body:{Body}", JsonConvert.SerializeObject(body));

            var eventType = platform.EventTypes.FirstOrDefault(x => x.Id.Equals(body.EventTypeId, StringComparison.OrdinalIgnoreCase));
            var producingEndpoint = platform.GetProducers(eventType).FirstOrDefault();
            if (producingEndpoint == null)
            {
                logger.LogError("Could not find any producers for the given event: {EventTypeId}", body.EventTypeId);
                return new BadRequestObjectResult("Could not find any producers for the given event");
            }

            if (eventType == null)
            {
                logger.LogError("Could not find event type: {EventTypeId}", body.EventTypeId);
                return new BadRequestObjectResult("Could not find event type: " + body.EventTypeId);
            }

            if (string.IsNullOrWhiteSpace(body.EventContent))
            {
                logger.LogError("Event content is empty");
                return new BadRequestObjectResult("Event content is empty" + body.EventContent);
            }
            var type = eventType.GetEventClassType();
            try
            {
                var @event = (Core.Events.IEvent)JsonConvert.DeserializeObject(Convert.ToString(body.EventContent), type);
                var validationResult = @event.TryValidate();
                if (validationResult.IsValid)
                {
                    object data = new
                    {
                        TopicName = producingEndpoint.Id,
                        EventType = eventType.Id,
                        EventMessage = body.EventContent,
                        SessionId = @event.GetSessionId(),
                        CorrelationId = Guid.NewGuid().ToString()
                    };
                    var json = JsonConvert.SerializeObject(data);
                    var stringContent = new StringContent(json, UnicodeEncoding.UTF8, "application/json");
                    var client = new HttpClient();
                    var response = await client.PostAsync(
                        new Uri(configuration.GetValue<string>("EventPublisherUri")), stringContent);

                    if (!response.IsSuccessStatusCode)
                        return new BadRequestObjectResult($"Could not publish the event on the Event Publisher");

                    return new OkResult();
                }
                string errorMessage = validationResult.ValidationResults.Select(x => x.ErrorMessage).Aggregate("", (current, next) => current + ", " + next);
                return new BadRequestObjectResult($"\"Validation failed. Event does not fullfill the scheme '{type.Name}' Error: '{errorMessage}'\"");
            }
            catch (JsonReaderException e)
            {
                logger.LogError("Could not parse the event content. Exception: {ExceptionMessage}", e.Message);
                return new BadRequestObjectResult($"\"Could not parse the value '{e.Path}'\"");
            }
            catch (Exception e)
            {
                logger.LogError("Could not compose event. Exception: {ExceptionMessage}", e.Message);
                return new BadRequestObjectResult("Could not compose event.");
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
                    MessageEntity origMessage = await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId);
                    eventTypeId = origMessage.EventTypeId;
                }
            }

            var messageAuditEntity = authorizationService.GetMessageAuditEntity(MessageAuditType.ResubmitWithChanges);

            if (!authorizationService.IsManagerOfEndpoint(endpoint))
                throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");

            await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, body.EventContent);
            await cosmosClient.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
            await cosmosClient.StoreMessageAudit(eventId, messageAuditEntity);

            return new OkResult();
        }

        public async Task<ActionResult<IEnumerable<BlockedEvent>>> GetEventBlockedIdAsync(string endpointId, string sessionId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var blockedEvents = (await cosmosClient.GetBlockedEventsOnSession(endpointId, sessionId))
                        .Select(Mapper.BlockedEventFromBlockedMessageEvent)
                        .ToList();

                return blockedEvents;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
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
        }

        public async Task<ActionResult<SearchResponse>> PostApiEventEndpointIdGetByFilterAsync(SearchRequest body, string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (!endpointIdValid)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            try
            {
                var filter = Mapper.MapFilter(body.EventFilter);
                filter.EndPointId = endpointId;  // Use validated URL parameter instead of body value
                var reponse = await cosmosClient.GetEventsByFilter(filter, body.ContinuationToken, body.MaxSearchItemsCount);
                return new SearchResponse
                {
                    Events = reponse.Events
                    .Select(Mapper.EventFromMessageStoreEvent)
                    .ToList(),
                    ContinuationToken = reponse.ContinuationToken
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }
        private async Task<MessageEntity> GetMessageWithFallback(string eventId, string messageId)
        {
            var message = await cosmosClient.GetMessage(eventId, messageId);
            if (message != null) return message;

            // Fallback: search per-endpoint containers for the event data
            foreach (var ep in platform.Endpoints)
            {
                try
                {
                    var unresolvedEvent = await cosmosClient.GetEvent(ep.Id, eventId);
                    if (unresolvedEvent != null)
                    {
                        logger.LogInformation("Message {MessageId} not found in messages container, using fallback from endpoint {EndpointId}", messageId, ep.Id);
                        return MessageEntityFromUnresolvedEvent(unresolvedEvent);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Fallback lookup failed for endpoint {EndpointId}", ep.Id);
                }
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
            };
        }
    }
}
