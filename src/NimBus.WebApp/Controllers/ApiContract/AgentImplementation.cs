using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NimBus.Core;
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
        private readonly IEventSchemaStore _schemas;
        private readonly IPlatform _platform;
        private readonly IAgentEventPublisher _publisher;
        private readonly ILogger<AgentImplementation> _logger;

        public AgentImplementation(
            IEventSchemaStore schemas,
            IPlatform platform,
            IAgentEventPublisher publisher,
            ILogger<AgentImplementation> logger)
        {
            _schemas = schemas;
            _platform = platform;
            _publisher = publisher;
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

            var schema = new EventSchema
            {
                EventTypeId = body.EventTypeId,
                Name = body.Name ?? body.EventTypeId,
                JsonSchema = body.JsonSchema,
                Description = body.Description,
                SessionKeyPath = body.SessionKeyPath,
                Version = 1,
                AgentId = "demo-agent",   // TODO(spec 022 Task 11): replace with API-key identity
                CreatedBy = "demo-agent", // TODO(spec 022 Task 11): replace with API-key identity
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

        // TODO(spec 022 Task 10): implement
        public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body)
            => Task.FromResult<IActionResult>(new StatusCodeResult(501));

        // ── GET /api/agent/receive ────────────────────────────────────────────

        // TODO(spec 022 Task 10): implement
        public Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
            => Task.FromResult<ActionResult<AgentReceivedMessage>>(new StatusCodeResult(501));

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

        // TODO(spec 022 Task 11): implement
        public Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body)
            => Task.FromResult<IActionResult>(new StatusCodeResult(501));
    }
}
