using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace NimBus.MappingExecutor;

/// <summary>
/// Parks a source message as <see cref="HandlerOutcome.PendingHandoff"/> using the existing
/// spec-022 handoff mechanism so it is recoverable via the WebApp Resubmit/Skip flow (spec 023).
/// Delegates to <see cref="MarkPendingHandoffJsonHandler"/> to reuse the same plumbing that the
/// Agent Zone uses — the message ends up <c>Pending+Handoff</c> in the store, the session is
/// blocked, and the Service Bus message is completed without holding a lock.
/// </summary>
public sealed class HandoffParkSink : IMappingParkSink
{
    private readonly ILogger<HandoffParkSink> _logger;

    /// <summary>Initialises the sink with its logger.</summary>
    public HandoffParkSink(ILogger<HandoffParkSink> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task Park(IMessageContext context, string reason, CancellationToken ct)
    {
        _logger.LogWarning("Parking message {EventTypeId} (session {SessionId}): {Reason}",
            context.MessageContent?.EventContent?.EventTypeId,
            context.SessionId,
            reason);

        // Reuse the exact same mechanism as MarkPendingHandoffJsonHandler:
        // wrap the raw IMessageContext in an EventHandlerContext and call
        // MarkPendingHandoff. StrictMessageHandler observes HandlerOutcome.PendingHandoff
        // after the handler returns, emits a PendingHandoffResponse, blocks the session,
        // and completes the Service Bus message — identical to the Agent Zone park path.
        new EventHandlerContext(context).MarkPendingHandoff(reason);
        return Task.CompletedTask;
    }
}
