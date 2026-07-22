using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    public const int MaximumMessageIdLength = InboxDuplicateDetector.MaximumMessageIdLength;

    /// <summary>The stable Resolver reason used for duplicate skips.</summary>
    public const string DuplicateReason = "DuplicateDetected";

    private const string RecordOperation = "record";
    private readonly IEventContextHandler _inner;
    private readonly IInboxStore _inboxStore;
    private readonly InboxDuplicateDetector _duplicateDetector;
    private readonly bool _checkHandledUpstream;
    private readonly ILogger<InboxMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxMiddleware"/> class.
    /// </summary>
    /// <param name="inner">The real event context handler.</param>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="lifecycleNotifier">The optional lifecycle notifier.</param>
    /// <param name="logger">The optional structured logger.</param>
    /// <param name="checkHandledUpstream">
    /// When <see langword="true"/>, the pre-dispatch duplicate check is skipped because the
    /// hosting composition already runs it (via the <see cref="InboxDuplicateDetector"/> handed
    /// to <c>StrictMessageHandler</c>) before this decorator is reached, and the middleware only
    /// records successes. This keeps a fresh delivery at exactly one store check and one record.
    /// </param>
    public InboxMiddleware(
        IEventContextHandler inner,
        IInboxStore inboxStore,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        ILogger<InboxMiddleware>? logger = null,
        bool checkHandledUpstream = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _logger = logger ?? NullLogger<InboxMiddleware>.Instance;
        _duplicateDetector = new InboxDuplicateDetector(inboxStore, lifecycleNotifier, _logger);
        _checkHandledUpstream = checkHandledUpstream;
    }

    /// <inheritdoc />
    public async Task Handle(
        IMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = _duplicateDetector.GetIdentityOrBypass(context);
        if (identity is null)
        {
            await _inner.Handle(context, cancellationToken);
            return;
        }

        if (!_checkHandledUpstream
            && await _duplicateDetector.IsDuplicateAsync(context, cancellationToken))
        {
            return;
        }

        await _inner.Handle(context, cancellationToken);

        if (context.HandlerOutcome != HandlerOutcome.Default)
        {
            // A pending handoff is not durable yet: the PendingHandoffResponse and the session
            // block still happen downstream, and both must be recreatable when a crash forces
            // redelivery. Recording here would turn that redelivery into a duplicate skip that
            // never re-establishes the pending state, so only plain successes are recorded.
            _logger.LogInformation(
                "Inbox record skipped for handler outcome {HandlerOutcome}. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, MessageId:{MessageId}",
                context.HandlerOutcome,
                identity.Value.EndpointId,
                context.EventTypeId,
                identity.Value.MessageId);
            return;
        }

        try
        {
            await _inboxStore.RecordProcessedAsync(
                identity.Value.EndpointId,
                identity.Value.MessageId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw _duplicateDetector.CreateStoreException(RecordOperation);
        }
    }
}
