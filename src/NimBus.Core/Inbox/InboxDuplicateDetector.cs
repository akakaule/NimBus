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
/// Consults the inbox store for previously processed messages and emits the duplicate-skip
/// signals (handler outcome, telemetry, structured log, lifecycle notification).
/// </summary>
/// <remarks>
/// The check is shared by two call sites: <see cref="InboxMiddleware"/> at the handler seam
/// (standalone compositions only), and <c>StrictMessageHandler</c> before its session-state
/// guards — so a redelivered duplicate whose session state has already moved on (for example a
/// successfully handled RetryRequest that unblocked its session) is still surfaced as a
/// duplicate rather than falling into a state-guard branch that hides it. Hosted compositions
/// run the check exclusively in <c>StrictMessageHandler</c> and configure the middleware as
/// record-only, keeping a fresh delivery at one check plus one record.
/// </remarks>
public sealed class InboxDuplicateDetector
{
    /// <summary>The maximum message-id length supported by inbox providers.</summary>
    public const int MaximumMessageIdLength = 512;

    /// <summary>The maximum endpoint-id length supported by inbox providers.</summary>
    public const int MaximumEndpointIdLength = 260;

    private const string CheckOperation = "check";
    private readonly IInboxStore _inboxStore;
    private readonly MessageLifecycleNotifier? _lifecycleNotifier;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxDuplicateDetector"/> class.
    /// </summary>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="lifecycleNotifier">The optional lifecycle notifier.</param>
    /// <param name="logger">The optional structured logger.</param>
    public InboxDuplicateDetector(
        IInboxStore inboxStore,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        ILogger? logger = null)
    {
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _lifecycleNotifier = lifecycleNotifier;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Determines whether the message was already processed successfully on this endpoint.
    /// When it was, sets <see cref="IMessageContext.HandlerOutcome"/> to
    /// <see cref="HandlerOutcome.DuplicateDetected"/> and emits the duplicate-skip signals.
    /// A message without a usable deduplication identity is never treated as a duplicate.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> when the message is a previously processed duplicate.</returns>
    /// <exception cref="InboxStoreException">The store check failed; the message must be retried.</exception>
    public async Task<bool> IsDuplicateAsync(
        IMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = GetIdentityOrBypass(context);
        if (identity is null)
            return false;

        var (endpointId, messageId) = identity.Value;
        bool hasProcessed;
        try
        {
            hasProcessed = await _inboxStore.HasProcessedAsync(endpointId, messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw CreateStoreException(CheckOperation);
        }

        if (!hasProcessed)
            return false;

        context.HandlerOutcome = HandlerOutcome.DuplicateDetected;
        NimBusMeters.InboxDuplicatesDetected.Add(1);
        _logger.LogInformation(
            "Inbox duplicate detected. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            endpointId,
            context.EventTypeId,
            context.GetEventIdOrDefault(),
            messageId,
            context.GetSessionIdOrDefault());
        await NotifyDuplicateDetectedBestEffort(context, cancellationToken);
        return true;
    }

    /// <summary>
    /// Resolves the (endpoint, message) deduplication identity, or <see langword="null"/> when
    /// the message cannot participate in deduplication. Identity is read through the
    /// non-throwing accessors — a real Service Bus context throws on a missing MessageId, and a
    /// message without one must bypass deduplication and still reach the handler.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The deduplication identity, or <see langword="null"/> to bypass the inbox.</returns>
    internal (string EndpointId, string MessageId)? GetIdentityOrBypass(IMessageContext context)
    {
        var messageId = context.GetMessageIdOrDefault();
        var endpointId = context.GetEndpointIdOrDefault();
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(endpointId))
        {
            _logger.LogWarning(
                "Inbox deduplication bypassed because MessageId or endpoint identity is missing or blank. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}",
                endpointId,
                context.EventTypeId);
            return null;
        }

        if (messageId.Length > MaximumMessageIdLength || endpointId.Length > MaximumEndpointIdLength)
        {
            _logger.LogWarning(
                "Inbox deduplication bypassed because MessageId length {MessageIdLength} or endpoint length {EndpointIdLength} exceeds the supported maximum ({MaximumMessageIdLength}/{MaximumEndpointIdLength}). EndpointId:{EndpointId}, EventTypeId:{EventTypeId}",
                messageId.Length,
                endpointId.Length,
                MaximumMessageIdLength,
                MaximumEndpointIdLength,
                endpointId,
                context.EventTypeId);
            return null;
        }

        return (endpointId, messageId);
    }

    /// <summary>
    /// Records the store failure signals and returns the sanitized transient exception.
    /// Provider details are intentionally dropped so they cannot leak through settlement paths.
    /// </summary>
    /// <param name="operation">The bounded operation tag (<c>check</c> or <c>record</c>).</param>
    /// <returns>The sanitized exception to throw.</returns>
    internal InboxStoreException CreateStoreException(string operation)
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
                context.GetEndpointIdOrDefault(),
                context.EventTypeId);
        }
    }
}
