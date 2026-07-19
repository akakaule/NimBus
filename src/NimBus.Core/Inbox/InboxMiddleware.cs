using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Inbox;

/// <summary>
/// Decorates event dispatch with record-on-success inbox deduplication.
/// </summary>
/// <remarks>
/// This component decorates <see cref="IEventContextHandler"/> rather than
/// <see cref="IMessagePipelineBehavior"/> because the generic message-pipeline terminal publishes
/// the Resolver response and settles the broker message. Inbox recording must complete, or fail,
/// before both operations so a record failure leaves the message available for retry.
/// </remarks>
public sealed class InboxMiddleware : IEventContextHandler
{
    /// <summary>The maximum message-id length supported by inbox providers.</summary>
    public const int MaximumMessageIdLength = 512;

    /// <summary>The stable Resolver reason used for duplicate skips.</summary>
    public const string DuplicateReason = "DuplicateDetected";

    private const string CheckOperation = "check";
    private const string RecordOperation = "record";
    private readonly IEventContextHandler _inner;
    private readonly IInboxStore _inboxStore;
    private readonly MessageLifecycleNotifier? _lifecycleNotifier;
    private readonly ILogger<InboxMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxMiddleware"/> class.
    /// </summary>
    /// <param name="inner">The real event context handler.</param>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="lifecycleNotifier">The optional lifecycle notifier.</param>
    /// <param name="logger">The optional structured logger.</param>
    public InboxMiddleware(
        IEventContextHandler inner,
        IInboxStore inboxStore,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        ILogger<InboxMiddleware>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _lifecycleNotifier = lifecycleNotifier;
        _logger = logger ?? NullLogger<InboxMiddleware>.Instance;
    }

    /// <inheritdoc />
    public async Task Handle(
        IMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            _logger.LogWarning(
                "Inbox deduplication bypassed because MessageId is missing or blank. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}",
                context.To,
                context.EventTypeId);
            await _inner.Handle(context, cancellationToken);
            return;
        }

        if (messageId.Length > MaximumMessageIdLength)
        {
            _logger.LogWarning(
                "Inbox deduplication bypassed because MessageId length {MessageIdLength} exceeds the supported maximum {MaximumMessageIdLength}. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}",
                messageId.Length,
                MaximumMessageIdLength,
                context.To,
                context.EventTypeId);
            await _inner.Handle(context, cancellationToken);
            return;
        }

        bool hasProcessed;
        try
        {
            hasProcessed = await _inboxStore.HasProcessedAsync(messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw CreateStoreException(CheckOperation);
        }

        if (hasProcessed)
        {
            context.HandlerOutcome = HandlerOutcome.DuplicateDetected;
            NimBusMeters.InboxDuplicatesDetected.Add(1);
            _logger.LogInformation(
                "Inbox duplicate detected. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                context.To,
                context.EventTypeId,
                context.EventId,
                messageId,
                context.SessionId);
            await NotifyDuplicateDetectedBestEffort(context, cancellationToken);
            return;
        }

        await _inner.Handle(context, cancellationToken);

        try
        {
            await _inboxStore.RecordProcessedAsync(messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw CreateStoreException(RecordOperation);
        }
    }

    private InboxStoreException CreateStoreException(string operation)
    {
        NimBusMeters.InboxOperationFailed.Add(
            1,
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreOperation, operation));
        _logger.LogError(
            "Inbox store operation {InboxOperation} failed; the message will be retried.",
            operation);
        return new InboxStoreException();
    }

    private async Task NotifyDuplicateDetectedBestEffort(
        IMessageContext context,
        CancellationToken cancellationToken)
    {
        if (_lifecycleNotifier?.HasObservers != true)
            return;

        try
        {
            await _lifecycleNotifier.NotifyDuplicateDetected(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "A duplicate lifecycle observer failed. Duplicate processing will continue. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}",
                context.To,
                context.EventTypeId);
        }
    }
}
