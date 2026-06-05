using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
// Aliased to disambiguate from the NSwag-generated NimBus.WebApp.ManagementApi.* types.
using CoreMessage = NimBus.Core.Messages.Message;
using CoreMessageType = NimBus.Core.Messages.MessageType;
using CoreMessageContent = NimBus.Core.Messages.MessageContent;
using CoreEventContent = NimBus.Core.Messages.EventContent;

namespace NimBus.WebApp.Controllers.ApiContract
{
    public class AgentImplementation : IAgentApiController
    {
        // Header carrying a demo-grade agent identity. Full API-key auth is deferred;
        // an X-Agent-Id header is the stand-in for v1 (spec 022).
        private const string AgentIdHeader = "X-Agent-Id";
        private const string DefaultAgentId = "demo-agent";

        // The pending sub-status discriminator the Agent Zone subscriber parks events under.
        private const string HandoffSubStatus = "Handoff";

        private readonly IEventSchemaStore _schemas;
        private readonly IPlatform _platform;
        private readonly IAgentEventPublisher _publisher;
        private readonly INimBusMessageStore _store;
        private readonly IManagerClient _managerClient;
        private readonly IAgentSubscriptionRegistry _subscriptions;
        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AgentImplementation> _logger;

        public AgentImplementation(
            IEventSchemaStore schemas,
            IPlatform platform,
            IAgentEventPublisher publisher,
            INimBusMessageStore store,
            IManagerClient managerClient,
            IAgentSubscriptionRegistry subscriptions,
            IConfiguration config,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AgentImplementation> logger)
        {
            _schemas = schemas;
            _platform = platform;
            _publisher = publisher;
            _store = store;
            _managerClient = managerClient;
            _subscriptions = subscriptions;
            _config = config;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        // ── GET /api/agent/catalog ────────────────────────────────────────────

        public async Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
        {
            var schemas = await _schemas.GetSchemas();

            var catalog = new AgentCatalog
            {
                Endpoints = _platform.Endpoints.Select(e => e.Id).ToList(),
                EventTypes = schemas.Select(s => new EventTypeInfo
                {
                    EventTypeId = s.EventTypeId,
                    Name = s.Name,
                    JsonSchema = s.JsonSchema,
                    Description = s.Description,
                }).ToList(),
            };

            return new OkObjectResult(catalog);
        }

        // ── POST /api/agent/event-types ───────────────────────────────────────

        public async Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body)
        {
            if (string.IsNullOrWhiteSpace(body.EventTypeId))
                return new BadRequestObjectResult("eventTypeId is required.");

            if (string.IsNullOrWhiteSpace(body.JsonSchema))
                return new BadRequestObjectResult("jsonSchema is required.");

            var agentId = CurrentAgentId();
            var schema = new EventSchema
            {
                EventTypeId = body.EventTypeId,
                Name = body.Name ?? body.EventTypeId,
                JsonSchema = body.JsonSchema,
                Description = body.Description,
                SessionKeyPath = body.SessionKeyPath,
                Version = 1,
                AgentId = agentId,
                CreatedBy = agentId,
                CreatedUtc = DateTime.UtcNow,
            };

            try
            {
                var stored = await _schemas.DefineEventType(schema);

                return new OkObjectResult(new EventTypeInfo
                {
                    EventTypeId = stored.EventTypeId,
                    Name = stored.Name,
                    JsonSchema = stored.JsonSchema,
                    Description = stored.Description,
                });
            }
            catch (SchemaConflictException)
            {
                _logger.LogWarning("Schema conflict for event type {EventTypeId}", body.EventTypeId);
                return new ConflictResult();
            }
        }

        // ── POST /api/agent/subscribe ─────────────────────────────────────────

        public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body)
        {
            if (string.IsNullOrWhiteSpace(body?.EventTypeId))
                return Task.FromResult<IActionResult>(new BadRequestObjectResult("eventTypeId is required."));

            var agentId = CurrentAgentId();
            _subscriptions.Subscribe(agentId, body.EventTypeId);
            _logger.LogInformation("Agent {AgentId} subscribed to {EventTypeId}", agentId, body.EventTypeId);
            return Task.FromResult<IActionResult>(new OkResult());
        }

        // ── GET /api/agent/receive ────────────────────────────────────────────

        public async Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
        {
            var zoneId = AgentZone.ResolveEndpointId(_config);
            var wait = waitSeconds ?? 20;
            var deadline = DateTime.UtcNow.AddSeconds(wait);

            // Build the type filter once: explicit query param wins; otherwise fall back
            // to the agent's registered subscriptions; otherwise match anything.
            var subscribed = _subscriptions.GetSubscriptions(CurrentAgentId());
            Func<string, bool> matches;
            if (!string.IsNullOrWhiteSpace(eventTypeId))
                matches = t => string.Equals(t, eventTypeId, StringComparison.Ordinal);
            else if (subscribed.Count > 0)
                matches = t => t != null && subscribed.Contains(t);
            else
                matches = _ => true;

            do
            {
                UnresolvedEvent parked;
                try
                {
                    parked = (await _store.GetPendingEventsOnSession(zoneId))
                        .Where(e => string.Equals(e.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal))
                        .Where(e => matches(e.EventTypeId))
                        .FirstOrDefault();
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Zone container doesn't exist yet — nothing parked.
                    return new NoContentResult();
                }
                catch (EndpointNotFoundException)
                {
                    return new NoContentResult();
                }

                if (parked != null)
                {
                    return new OkObjectResult(new AgentReceivedMessage
                    {
                        EventTypeId = parked.EventTypeId,
                        Payload = parked.MessageContent?.EventContent?.EventJson,
                        Coordinates = new HandoffCoordinates
                        {
                            EventId = parked.EventId,
                            SessionId = parked.SessionId,
                            MessageId = parked.LastMessageId,
                            EventTypeId = parked.EventTypeId,
                            CorrelationId = parked.CorrelationId,
                            OriginatingMessageId = parked.OriginatingMessageId,
                        },
                    });
                }

                if (wait == 0)
                    break;

                await Task.Delay(500);
            }
            while (DateTime.UtcNow < deadline);

            return new NoContentResult();
        }

        // ── POST /api/agent/publish ───────────────────────────────────────────

        public async Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body)
        {
            if (string.IsNullOrWhiteSpace(body.EventTypeId))
                return new BadRequestObjectResult("eventTypeId is required.");
            if (string.IsNullOrWhiteSpace(body.Payload))
                return new BadRequestObjectResult("payload is required.");

            var schema = await _schemas.GetSchema(body.EventTypeId);
            if (schema == null)
                return new NotFoundObjectResult($"Unknown eventTypeId '{body.EventTypeId}'.");

            NJsonSchema.JsonSchema jsonSchema;
            try
            {
                jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(schema.JsonSchema);
            }
            // NJsonSchema throws assorted undocumented exception types on bad schema JSON
            // (JsonReaderException, InvalidOperationException, etc.) — catch-all is intentional.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registered schema for {EventTypeId} is not valid JSON Schema", body.EventTypeId);
                return new BadRequestObjectResult($"Registered schema for '{body.EventTypeId}' is not valid JSON Schema: {ex.Message}");
            }

            ICollection<NJsonSchema.Validation.ValidationError> errors;
            try
            {
                errors = jsonSchema.Validate(body.Payload);
            }
            // NJsonSchema throws assorted undocumented exception types on unparseable payload
            // JSON (JsonReaderException, etc.) — catch-all is intentional.
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Payload is not valid JSON: {ex.Message}");
            }

            if (errors.Count > 0)
                return new BadRequestObjectResult(errors.Select(e => $"{e.Path}: {e.Kind}").ToList());

            var message = new CoreMessage
            {
                To = body.EventTypeId,
                EventTypeId = body.EventTypeId,
                SessionId = string.IsNullOrWhiteSpace(body.SessionId) ? Guid.NewGuid().ToString() : body.SessionId,
                CorrelationId = Guid.NewGuid().ToString(),
                MessageId = Guid.NewGuid().ToString(),
                RetryCount = 0,
                MessageType = CoreMessageType.EventRequest,
                MessageContent = new CoreMessageContent
                {
                    EventContent = new CoreEventContent
                    {
                        EventTypeId = body.EventTypeId,
                        EventJson = body.Payload,
                    },
                },
            };

            await _publisher.PublishAsync(message);
            return new OkResult();
        }

        // ── POST /api/agent/settle ────────────────────────────────────────────

        public async Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body)
        {
            var coords = body?.Coordinates;
            if (coords == null || string.IsNullOrWhiteSpace(coords.EventId))
                return new BadRequestObjectResult("coordinates.eventId is required.");

            var zoneId = AgentZone.ResolveEndpointId(_config);

            UnresolvedEvent pending;
            try
            {
                pending = await _store.GetEvent(zoneId, coords.EventId);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Agent settle: event not found. ZoneId: {ZoneId}, EventId: {EventId}, Ex: {Exception}", zoneId, coords.EventId, e.Message);
                return new NotFoundObjectResult("Event not found");
            }

            if (pending == null)
                return new NotFoundObjectResult("Event not found");

            if (pending.ResolutionStatus != global::NimBus.MessageStore.ResolutionStatus.Pending
                || !string.Equals(pending.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Agent settle rejected: event is not a pending handoff. ZoneId: {ZoneId}, EventId: {EventId}, Status: {Status}, SubStatus: {SubStatus}",
                    zoneId, coords.EventId, pending.ResolutionStatus, pending.PendingSubStatus ?? "<null>");
                return new BadRequestObjectResult("Event is not a pending handoff.");
            }

            var pendingEntry = new MessageEntity
            {
                EventId = pending.EventId,
                MessageId = coords.MessageId,
                SessionId = pending.SessionId,
                CorrelationId = pending.CorrelationId,
                OriginatingMessageId = pending.OriginatingMessageId,
                EventTypeId = pending.EventTypeId,
                PendingSubStatus = pending.PendingSubStatus,
            };

            if (body.Outcome == AgentSettleRequestOutcome.Complete)
            {
                _logger.LogInformation("Agent settle (complete). ZoneId: {ZoneId}, EventId: {EventId}", zoneId, coords.EventId);
#pragma warning disable CS0618 // Manager-side settlement: the WebApp *is* the Manager. The
                // [Obsolete] hint steers adapters toward IHandoffClient; the manager path
                // takes the endpoint as a parameter and reuses the ServiceBusClient registered
                // in DI (mirrors EventImplementation.PostHandoffCompleteAsync).
                await _managerClient.CompleteHandoff(pendingEntry, zoneId, body.Result);
#pragma warning restore CS0618
                return new OkResult();
            }

            // Fail outcome
            if (string.IsNullOrWhiteSpace(body.ErrorText))
                return new BadRequestObjectResult("errorText is required when outcome is 'fail'.");

            _logger.LogInformation("Agent settle (fail). ZoneId: {ZoneId}, EventId: {EventId}, ErrorType: {ErrorType}", zoneId, coords.EventId, body.ErrorType);
#pragma warning disable CS0618 // See above — manager-side settlement.
            await _managerClient.FailHandoff(pendingEntry, zoneId, body.ErrorText, body.ErrorType);
#pragma warning restore CS0618
            return new OkResult();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Demo-grade agent identity: read the X-Agent-Id header, defaulting to
        // "demo-agent" when absent. Full API-key auth is deferred (spec 022).
        private string CurrentAgentId()
        {
            var header = _httpContextAccessor?.HttpContext?.Request?.Headers[AgentIdHeader].ToString();
            return string.IsNullOrWhiteSpace(header) ? DefaultAgentId : header;
        }
    }
}
