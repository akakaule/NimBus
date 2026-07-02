using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.Core.Transform;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.SDK.EventHandlers;

namespace NimBus.MappingExecutor;

/// <summary>Parks a source message for operator recovery with a reason (spec 023).</summary>
public interface IMappingParkSink
{
    /// <summary>Parks the message under the given <paramref name="reason"/> for operator recovery.</summary>
    Task Park(IMessageContext context, string reason, CancellationToken ct);
}

/// <summary>Publishes a transformed target event onto the bus (spec 023).</summary>
public interface IMappingTargetPublisher
{
    /// <summary>
    /// Publishes a transformed <paramref name="payload"/> as <paramref name="targetEventTypeId"/>
    /// carrying the source <paramref name="sessionId"/> to preserve ordering.
    /// </summary>
    Task Publish(string targetEventTypeId, string payload, string sessionId, CancellationToken ct);
}

/// <summary>
/// The single fallback handler registered on the Mapping Zone. Per message it resolves the
/// source EventTypeId, consults the mapping registry, and acts on the mapping's state. Never
/// calls an LLM.
/// </summary>
public sealed class MappingExecutorHandler : IEventJsonHandler
{
    private readonly IEventMappingStore _mappings;
    private readonly IEventSchemaStore _schemas;
    private readonly IMappingTransformEngine _engine;
    private readonly IMappingTargetPublisher _publisher;
    private readonly IMappingParkSink _park;
    private readonly ILogger<MappingExecutorHandler> _logger;

    /// <summary>Initialises the handler with its required dependencies.</summary>
    public MappingExecutorHandler(
        IEventMappingStore mappings, IEventSchemaStore schemas, IMappingTransformEngine engine,
        IMappingTargetPublisher publisher, IMappingParkSink park, ILogger<MappingExecutorHandler> logger)
    {
        _mappings = mappings;
        _schemas = schemas;
        _engine = engine;
        _publisher = publisher;
        _park = park;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        var source = context.MessageContent.EventContent.EventTypeId;
        var input = context.MessageContent.EventContent.EventJson;

        var active = await _mappings.GetActiveMappingForSource(source);
        if (active is null)
        {
            var any = (await _mappings.GetMappings()).Any(m => m.SourceEventTypeId == source);
            var parkReason = any ? "mapping is Paused/Stale" : "no mapping for source type";
            _logger.LogWarning("Parking message for {SourceEventTypeId}: {ParkReason}", source, parkReason);
            await _park.Park(context, parkReason, cancellationToken);
            return;
        }

        // Drift guard: input schema fingerprint must still match the one the mapping was authored against.
        var schema = await _schemas.GetSchema(source);
        if (schema is null || SchemaHash.Of(schema.JsonSchema) != active.SourceSchemaHash)
        {
            _logger.LogWarning("Source schema drifted for {SourceEventTypeId}; marking mapping {MappingId} Stale", source, active.Id);
            active.State = MappingState.Stale;
            await _mappings.SaveMapping(active);
            await _park.Park(context, "source schema drifted; mapping marked Stale", cancellationToken);
            return;
        }

        string output;
        try
        {
            output = _engine.Transform(active.Transform, input);
        }
        catch (MappingTransformException ex)
        {
            _logger.LogWarning(ex, "Transform error for mapping {MappingId}", active.Id);
            await _park.Park(context, $"transform error: {ex.Message}", cancellationToken);
            return;
        }

        var targetSchema = await _schemas.GetSchema(active.TargetEventTypeId);
        if (targetSchema is null)
        {
            await _park.Park(context, "target schema missing", cancellationToken);
            return;
        }

        NJsonSchema.JsonSchema jsonSchema;
        System.Collections.Generic.ICollection<NJsonSchema.Validation.ValidationError> errors;
        try
        {
            jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(targetSchema.JsonSchema);
            errors = jsonSchema.Validate(output);
        }
        // NJsonSchema throws assorted undocumented exception types on bad schema JSON
        // (JsonReaderException, InvalidOperationException, etc.). Schemas are immutable, so a
        // stored-but-unparseable target schema can never be fixed by retrying — park the message
        // for operator recovery instead of letting the pipeline mark it Failed / block the session.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Target schema for {TargetEventTypeId} is not valid JSON Schema (mapping {MappingId})", active.TargetEventTypeId, active.Id);
            await _park.Park(context, $"Target schema for '{active.TargetEventTypeId}' is not valid JSON Schema: {ex.Message}", cancellationToken);
            return;
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Transformed output for mapping {MappingId} failed target schema ({ErrorCount} error(s))", active.Id, errors.Count);
            await _park.Park(context, "transformed output failed target schema", cancellationToken);
            return;
        }

        await _publisher.Publish(active.TargetEventTypeId, output, SessionIdOf(context), cancellationToken);
    }

    private static string SessionIdOf(IMessageContext context)
    {
        // SessionId is defined on IMessage (the base of IMessageContext) and is populated by
        // the transport adapter. Carry it forward so the target event follows the same session
        // partition and preserves FIFO ordering on the target topic.
        var sessionId = context.SessionId;
        return string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString() : sessionId;
    }
}
