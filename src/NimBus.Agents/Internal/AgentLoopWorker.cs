using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace NimBus.Agents.Internal;

/// <summary>
/// The generic hosted loop driving an <see cref="IAgentHandler{TInput}"/>: subscribe (best-effort)
/// → define outputs (once) → receive → deserialize → handle → publish → settle. Per-message logic
/// is in <see cref="ProcessNextAsync"/> so it is unit-testable against a fake gateway/handler.
/// </summary>
/// <typeparam name="TInput">The deserialized source-event payload type.</typeparam>
internal sealed class AgentLoopWorker<TInput> : BackgroundService
{
    private readonly IAgentBusGateway _bus;
    private readonly IAgentHandler<TInput> _handler;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentLoopWorker<TInput>> _logger;

    // Guards DefineEventTypeAsync so the (idempotent) output schema definitions are attempted once.
    private bool _outputsDefined;

    public AgentLoopWorker(IAgentBusGateway bus, IAgentHandler<TInput> handler, AgentOptions options, ILogger<AgentLoopWorker<TInput>> logger)
    {
        _bus = bus;
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = _options.SourceEventTypeId!;

        // Register interest so receive is server-side filtered. Best-effort: receive still works
        // because it passes the type explicitly.
        try
        {
            await _bus.SubscribeAsync(source, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Subscribed to {EventTypeId}.", source);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscribe to {EventTypeId} failed; continuing with explicit-type receive.", source);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent loop iteration failed; backing off before retry.");
                try { await Task.Delay(_options.ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Runs one receive → handle → publish → settle cycle. Returns <c>true</c> if a message was
    /// processed, <c>false</c> if none was parked.
    /// </summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var msg = await _bus.ReceiveAsync(_options.SourceEventTypeId, _options.ReceiveWaitSeconds, ct).ConfigureAwait(false);
        if (msg is null)
            return false;

        _logger.LogInformation(
            "Received {EventTypeId} event {EventId} on session {SessionId}.",
            msg.EventTypeId, msg.Coordinates.EventId, msg.Coordinates.SessionId);

        await EnsureOutputsDefinedAsync(ct).ConfigureAwait(false);

        TInput input;
        try
        {
            input = JsonConvert.DeserializeObject<TInput>(msg.Payload)
                ?? throw new InvalidOperationException($"{msg.EventTypeId} payload deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Poison message: this payload will never deserialize, so retrying forever is pointless.
            // Settle it as failed (with a clear error) to remove it from the parked queue.
            _logger.LogError(ex,
                "Could not deserialize {EventTypeId} into {Type}; settling handoff {EventId} as failed (poison message).",
                msg.EventTypeId, typeof(TInput).Name, msg.Coordinates.EventId);
            await SettleAsync(
                msg.Coordinates,
                AgentResult.Fail($"Could not deserialize payload into {typeof(TInput).Name}: {ex.Message}", "DeserializeError"),
                ct).ConfigureAwait(false);
            return true;
        }

        var context = new AgentContext<TInput>
        {
            Input = input,
            RawPayload = msg.Payload,
            EventTypeId = msg.EventTypeId,
            Coordinates = msg.Coordinates,
        };

        var result = await _handler.HandleAsync(context, ct).ConfigureAwait(false);

        // Publish everything first; if any publish throws, the exception propagates BEFORE settle,
        // so the handoff stays parked rather than being marked done with missing output.
        foreach (var publish in result.Publishes)
        {
            var sessionId = publish.SessionId ?? msg.Coordinates.SessionId;
            await _bus.PublishAsync(publish.EventTypeId, publish.Payload, sessionId, ct).ConfigureAwait(false);
            _logger.LogInformation("Published {EventTypeId} on session {SessionId}.", publish.EventTypeId, sessionId);
        }

        await SettleAsync(msg.Coordinates, result, ct).ConfigureAwait(false);
        return true;
    }

    private async Task SettleAsync(HandoffCoordinates coordinates, AgentResult result, CancellationToken ct)
    {
        try
        {
            await _bus.SettleAsync(coordinates, result.IsSuccess, result.Result, result.ErrorText, result.ErrorType, ct).ConfigureAwait(false);
            _logger.LogInformation("Settled handoff {EventId} as {Outcome}.", coordinates.EventId, result.IsSuccess ? "complete" : "fail");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // At-least-once: a concurrent receive already settled this handoff, so the
            // Pending+Handoff guard returns 400. Safe to ignore.
            _logger.LogInformation(
                "Settle for handoff {EventId} returned 400 (already settled by a concurrent receive); ignoring.",
                coordinates.EventId);
        }
    }

    private async Task EnsureOutputsDefinedAsync(CancellationToken ct)
    {
        if (_outputsDefined)
            return;

        foreach (var output in _options.Outputs)
        {
            await _bus.DefineEventTypeAsync(output.EventTypeId, output.JsonSchema, output.Name, output.Description, output.SessionKeyPath, ct).ConfigureAwait(false);
            _logger.LogInformation("Ensured event type {EventTypeId} is defined.", output.EventTypeId);
        }

        _outputsDefined = true;
    }
}
