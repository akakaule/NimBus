using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using System.Net;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    public async Task<IActionResult> PostMessageAuditAsync(MessageAudit body, string eventId)
    {
        // Writing an operator comment/audit row is an action, not a read.
        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor))
            return new ForbidResult();

        try
        {
            var audit = Mapper.MessageAuditEntityFromMessageAudit(body);
            await messageStore.StoreMessageAudit(eventId, audit);
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
        var history = await messageStore.GetEventHistory(eventId);
        MessageEntity? latestRequest = LatestRequestMessageWithPayload(history);
        // A CloudEvent may be stored without native EventContent. The subscriber's
        // PendingHandoffResponse preserves the validated event JSON it parked.
        var parkedPayload = history
            .Where(message => message.MessageType == Core.Messages.MessageType.PendingHandoffResponse
                && !string.IsNullOrEmpty(message.MessageContent?.EventContent?.EventJson))
            .OrderByDescending(message => message.EnqueuedTimeUtc)
            .FirstOrDefault();
        MessageEntity requestMessage = latestRequest ?? parkedPayload ?? errorResponse;

        eventTypeId = errorResponse.EventTypeId;
        if (string.IsNullOrEmpty(eventTypeId))
        {
            MessageEntity typeSource = latestRequest ?? parkedPayload
                ?? await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId)
                ?? errorResponse;
            eventTypeId = !string.IsNullOrWhiteSpace(typeSource.EventTypeId)
                ? typeSource.EventTypeId
                : typeSource.MessageContent?.EventContent?.EventTypeId!;
        }

        if (BlockedEventRules.IsSelfOriginating(errorResponse.OriginatingMessageId))
        {
            endpoint = errorResponse.To;
        }
        else
        {
            endpoint = errorResponse.From;
        }

        var eventJson = requestMessage.MessageContent?.EventContent?.EventJson!;

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpoint))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.Resubmit, httpContextAccessor.HttpContext,
                accessDenied: true, eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            return new ForbidResult();
        }

        // Deliberately sequential — do not parallelize. ArchiveFailedEvent
        // soft-deletes the event (deleted=true + 30d TTL); if the publish
        // fails, the event must remain visible in the failed list. Running
        // these concurrently would archive events whose resubmit never left.
        await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, eventJson);
        await messageStore.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
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

        if (BlockedEventRules.IsSelfOriginating(errorResponse.OriginatingMessageId))
        {
            endpoint = errorResponse.To;
        }
        else
        {
            endpoint = errorResponse.From;
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpoint))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.Skip, httpContextAccessor.HttpContext,
                accessDenied: true, eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            return new ForbidResult();
        }

        await managerClient.Skip(errorResponse, endpoint, eventTypeId);
        await auditLogService.LogAuditAsync(MessageAuditType.Skip, httpContextAccessor.HttpContext,
            eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
        await messageStore.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);

        return new OkResult();
    }

    public async Task<IActionResult> PostReportEventAsync(ReportEventRequest body, string endpointId, string eventId)
    {
        // `Reported` is modelled nullable so an omitted value binds as null
        // (MVC binds with System.Text.Json, which ignores the generated
        // Newtonsoft Required attribute) — an empty `{}` body must be a 400,
        // not a silent "clear the marker".
        if (body?.Reported is not bool reported)
            return new BadRequestObjectResult("The 'reported' field is required.");
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(eventId))
            return new BadRequestObjectResult("endpointId and eventId are required.");

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.ReportEvent, httpContextAccessor.HttpContext,
                accessDenied: true, eventId: eventId, endpointId: endpointId);
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        // Store under the platform's canonical endpoint casing: authorization
        // and existence checks are case-insensitive, but Cosmos partitions
        // (and the enrichment lookups) match the endpoint id exactly — a
        // lowercase request must not create a marker searches never find.
        endpointId = CanonicalEndpointId(endpointId);

        string ticketId = null;
        if (reported && !string.IsNullOrWhiteSpace(body.TicketId))
        {
            ticketId = body.TicketId.Trim();
            if (!TicketIdPattern.IsMatch(ticketId))
            {
                return new BadRequestObjectResult("Ticket id may use letters, digits, '.', '_' and '-' (max 64 chars).");
            }
        }

        var reportedBy = authorizationService.GetCurrentUserName() ?? "anonymous";
        await messageStore.SetEventReport(endpointId, eventId, reported, reportedBy, ticketId);
        await auditLogService.LogAuditAsync(MessageAuditType.ReportEvent, httpContextAccessor.HttpContext,
            eventId: eventId, endpointId: endpointId,
            data: JsonConvert.SerializeObject(new { reported, ticketId }));

        return new OkResult();
    }

    // Generic external-ticket reference: a sane cross-tool subset (Jira keys,
    // ServiceNow INC numbers, plain ids). Mirrored by the frontend's
    // normalizeTicketId and the EventReports TicketId column width (64).
    private static readonly System.Text.RegularExpressions.Regex TicketIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<ActionResult<DeferredReprocessResult>> PostReprocessDeferredAsync(string endpointId, string sessionId)
    {
        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
            return new ForbidResult();

        var result = await adminService.ReprocessDeferredAsync(endpointId, sessionId);
        return new OkObjectResult(result);
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

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, producingEndpoint.Id))
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
        if (BlockedEventRules.IsSelfOriginating(errorResponse.OriginatingMessageId))
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
            eventTypeId = await ResolveServerEventTypeIdAsync(eventId, errorResponse);
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpoint))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
                accessDenied: true, data: JsonConvert.SerializeObject(body),
                eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
        }

        // A non-PiiReader was served a payload whose [Sensitive] fields were masked.
        // Resubmitting that body verbatim would overwrite the real values with the
        // mask token, so reject it and make them re-enter the sensitive fields.
        // The type id is always resolved server-side here: trusting body.EventTypeId
        // would let a caller name a type with no [Sensitive] members to skip the check.
        if (!await authorizationService.CanReadPiiAsync())
        {
            var serverEventTypeId = string.IsNullOrEmpty(body.EventTypeId)
                ? eventTypeId
                : await ResolveServerEventTypeIdAsync(eventId, errorResponse);

            // Fail closed: with no resolvable type we cannot prove the body is clean.
            if (string.IsNullOrEmpty(serverEventTypeId))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
                    accessDenied: true, data: JsonConvert.SerializeObject(body),
                    eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                return new BadRequestObjectResult(
                    "Resubmit rejected: the event type could not be resolved server-side, so the payload cannot be checked for masked PII. Ask a site Owner for the PiiReader role on the Access Control page.");
            }

            if (masker.ContainsRedactPlaceholder(serverEventTypeId, body.EventContent))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
                    accessDenied: true, data: JsonConvert.SerializeObject(body),
                    eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                return new BadRequestObjectResult(
                    "Resubmit rejected: sensitive fields still contain the mask placeholder. Re-enter every masked value, or ask a site Owner for the PiiReader role to resubmit the payload unmodified.");
            }
        }

        // The body may have round-tripped the $piiMasked sidecar marker; strip it so
        // the marker never leaks into the actual event payload.
        var forwardedContent = masker.StripMaskedMarker(body.EventContent);

        // Deliberately sequential — do not parallelize. ArchiveFailedEvent
        // soft-deletes the event (deleted=true + 30d TTL); if the publish
        // fails, the event must remain visible in the failed list.
        await managerClient.Resubmit(errorResponse, endpoint, eventTypeId, forwardedContent);
        await messageStore.ArchiveFailedEvent(eventId, errorResponse.SessionId, endpoint);
        await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext,
            data: JsonConvert.SerializeObject(body),
            eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);

        return new OkResult();
    }

    // Resolves the event type id from stored messages only, never from the request
    // body. Same source as the frontend's resubmit prefill: the latest request
    // message that carries the event payload (the original EventRequest, or a later
    // resubmission/retry). For a failed hand-off the terminal ErrorResponse carries
    // no event type, so resolve it from the request history rather than the
    // originating message. Falls back to the originating-message lookup when no
    // request message carries a payload, and finally to the terminal message itself.
    private async Task<string> ResolveServerEventTypeIdAsync(string eventId, MessageEntity errorResponse)
    {
        if (!string.IsNullOrEmpty(errorResponse.EventTypeId))
            return errorResponse.EventTypeId;

        var history = await messageStore.GetEventHistory(eventId);
        MessageEntity requestMessage = LatestRequestMessageWithPayload(history)
            ?? await GetMessageWithFallback(eventId, errorResponse.OriginatingMessageId)
            ?? errorResponse;
        return !string.IsNullOrWhiteSpace(requestMessage.EventTypeId)
            ? requestMessage.EventTypeId
            : requestMessage.MessageContent?.EventContent?.EventTypeId!;
    }

    public async Task<IActionResult> DeleteEventInvalidIdAsync(string endpointId, string eventId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
            return new ForbidResult();

        try
        {
            var result = await messageStore.RemoveMessage(eventId, sessionId, endpointId);
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
}
