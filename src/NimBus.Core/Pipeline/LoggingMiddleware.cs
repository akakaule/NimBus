using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Middleware that logs message processing with timing, event metadata, and outcome.
/// </summary>
public sealed class LoggingMiddleware : IMessagePipelineBehavior
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing {MessageType} | EventType={EventTypeId} EventId={EventId} SessionId={SessionId}",
            context.MessageType, context.EventTypeId, context.EventId, context.SessionId);

        try
        {
            await next(context, cancellationToken);
            sw.Stop();

            _logger.LogInformation(
                "Completed {MessageType} in {ElapsedMs}ms | EventType={EventTypeId} EventId={EventId}",
                context.MessageType, sw.ElapsedMilliseconds, context.EventTypeId, context.EventId);
        }
        catch (Exception ex)
        {
            sw.Stop();

            _logger.LogError(ex,
                "Failed {MessageType} after {ElapsedMs}ms | EventType={EventTypeId} EventId={EventId}",
                context.MessageType, sw.ElapsedMilliseconds, context.EventTypeId, context.EventId);

            throw;
        }
    }
}
