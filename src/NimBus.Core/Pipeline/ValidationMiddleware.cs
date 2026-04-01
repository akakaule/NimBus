using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Middleware that validates message context before processing.
/// Rejects messages with missing critical fields by dead-lettering them.
/// </summary>
public sealed class ValidationMiddleware : IMessagePipelineBehavior
{
    private readonly ILogger<ValidationMiddleware> _logger;

    public ValidationMiddleware(ILogger<ValidationMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.EventId))
        {
            _logger.LogWarning("Message rejected: missing EventId (MessageId={MessageId})", context.MessageId);
            var reason = "Validation failed: EventId is required";
            await context.DeadLetter(reason, new InvalidOperationException(reason), cancellationToken);
            throw new MessageAlreadyDeadLetteredException(reason);
        }

        if (context.MessageType == MessageType.EventRequest && string.IsNullOrEmpty(context.EventTypeId))
        {
            _logger.LogWarning("EventRequest rejected: missing EventTypeId (EventId={EventId})", context.EventId);
            var reason = "Validation failed: EventTypeId is required for EventRequest";
            await context.DeadLetter(reason, new InvalidOperationException(reason), cancellationToken);
            throw new MessageAlreadyDeadLetteredException(reason);
        }

        await next(context, cancellationToken);
    }
}

/// <summary>
/// Thrown by middleware that has already dead-lettered a message.
/// MessageHandler recognizes this and fires lifecycle notifications
/// without attempting to dead-letter again.
/// </summary>
public sealed class MessageAlreadyDeadLetteredException : Exception
{
    public MessageAlreadyDeadLetteredException(string reason) : base(reason) { }
}
