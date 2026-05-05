using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.Transport.Abstractions;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Service Bus implementation of <see cref="ITransportSessionOps"/>. Wraps the
/// session-receiver / subscription-peek primitives the operator-tooling
/// (<c>nimbus-ops</c>) AdminService has historically called directly. Phase
/// 6.2 task #25 (1H) introduces this seam so RabbitMQ deployments can satisfy
/// the same operator surface; AdminService consumption is staged separately
/// to keep the WebApp test surface stable.
/// </summary>
public sealed class ServiceBusSessionOps : ITransportSessionOps
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusSessionOps>? _logger;

    public ServiceBusSessionOps(ServiceBusClient client, ILogger<ServiceBusSessionOps>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    public async Task<TransportSessionPreview> PreviewSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken)
    {
        // Service Bus exposes the per-session message count via the deferred
        // subscription peek. The endpoint subscription's session-state count
        // ought to be supplied by the caller (AdminService reads it from the
        // message store) so this operation focuses on the broker side only.
        long deferredSubscriptionCount = 0;
        try
        {
            var deferredReceiver = await _client.AcceptSessionAsync(
                endpointName, CoreConstants.DeferredSubscriptionName, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (deferredReceiver)
            {
                var peeked = await deferredReceiver.PeekMessagesAsync(100, cancellationToken: cancellationToken).ConfigureAwait(false);
                deferredSubscriptionCount = peeked.Count;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No session — 0 messages.
        }

        return new TransportSessionPreview(
            SessionId: sessionId,
            ActiveMessageCount: 0,
            DeferredMessageCount: deferredSubscriptionCount);
    }

    public async Task<TransportSessionPurgeResult> PurgeSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        long activeRemoved = 0;
        long deferredRemoved = 0;
        long deferredSubscriptionRemoved = 0;
        var sessionStateCleared = false;

        ServiceBusSessionReceiver? receiver = null;
        try
        {
            receiver = await _client.AcceptSessionAsync(endpointName, endpointName, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not accept SB session {SessionId} on {EndpointId}", sessionId, endpointName);
            errors.Add($"Could not accept session: {ex.Message}");
        }

        if (receiver is not null)
        {
            try
            {
                int batchCount;
                do
                {
                    var messages = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    batchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        try
                        {
                            await receiver.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
                            activeRemoved++;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to complete active message {MessageId}", message.MessageId);
                            errors.Add($"Active message {message.MessageId}: {ex.Message}");
                        }
                    }
                }
                while (batchCount > 0);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error removing active messages from session {SessionId}", sessionId);
                errors.Add($"Active messages error: {ex.Message}");
            }

            try
            {
                long? fromSequenceNumber = null;
                int peekCount;
                do
                {
                    var peeked = fromSequenceNumber.HasValue
                        ? await receiver.PeekMessagesAsync(100, fromSequenceNumber.Value, cancellationToken).ConfigureAwait(false)
                        : await receiver.PeekMessagesAsync(100, cancellationToken: cancellationToken).ConfigureAwait(false);
                    peekCount = peeked.Count;
                    if (peekCount > 0)
                        fromSequenceNumber = peeked[peekCount - 1].SequenceNumber + 1;
                    foreach (var message in peeked)
                    {
                        if (message.State != ServiceBusMessageState.Deferred) continue;
                        try
                        {
                            var deferredMessage = await receiver.ReceiveDeferredMessageAsync(message.SequenceNumber, cancellationToken).ConfigureAwait(false);
                            if (deferredMessage is not null)
                            {
                                await receiver.CompleteMessageAsync(deferredMessage, cancellationToken).ConfigureAwait(false);
                                deferredRemoved++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to complete deferred message {SequenceNumber}", message.SequenceNumber);
                            errors.Add($"Deferred message seq {message.SequenceNumber}: {ex.Message}");
                        }
                    }
                }
                while (peekCount > 0);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error removing deferred messages from session {SessionId}", sessionId);
                errors.Add($"Deferred messages error: {ex.Message}");
            }

            try
            {
                await receiver.SetSessionStateAsync(null, cancellationToken).ConfigureAwait(false);
                sessionStateCleared = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clear session state for {SessionId}", sessionId);
                errors.Add($"Clear session state: {ex.Message}");
            }

            await receiver.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            var deferredReceiver = await _client.AcceptSessionAsync(
                endpointName, CoreConstants.DeferredSubscriptionName, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (deferredReceiver)
            {
                int batchCount;
                do
                {
                    var messages = await deferredReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    batchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        await deferredReceiver.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
                        deferredSubscriptionRemoved++;
                    }
                }
                while (batchCount > 0);
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No deferred messages for this session — not an error.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error removing messages from Deferred subscription for session {SessionId}", sessionId);
            errors.Add($"Deferred subscription error: {ex.Message}");
        }

        return new TransportSessionPurgeResult(
            SessionId: sessionId,
            ActiveMessagesRemoved: activeRemoved,
            DeferredMessagesRemoved: deferredRemoved,
            DeferredSubscriptionMessagesRemoved: deferredSubscriptionRemoved,
            SessionStateCleared: sessionStateCleared,
            Errors: errors);
    }

    public async Task<TransportSubscriptionPreview> PreviewSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken)
    {
        var (purgeActive, purgeDeferred) = ResolveStates(states);

        await using var receiver = _client.CreateReceiver(endpointName, subscriptionName);
        long totalScanned = 0;
        long totalMatching = 0;
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        long fromSequenceNumber = 0;

        while (true)
        {
            var peeked = await receiver.PeekMessagesAsync(100, fromSequenceNumber, cancellationToken).ConfigureAwait(false);
            if (peeked.Count == 0) break;

            foreach (var msg in peeked)
            {
                totalScanned++;
                var stateMatch = (purgeActive && msg.State == ServiceBusMessageState.Active)
                              || (purgeDeferred && msg.State == ServiceBusMessageState.Deferred);
                if (stateMatch && (!enqueuedBeforeUtc.HasValue || msg.EnqueuedTime.UtcDateTime < enqueuedBeforeUtc.Value))
                {
                    totalMatching++;
                    sessionIds.Add(msg.SessionId ?? string.Empty);
                }
            }

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        return new TransportSubscriptionPreview(totalScanned, totalMatching, sessionIds.Count);
    }

    public async Task<TransportBulkResult> PurgeSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken)
    {
        var (purgeActive, purgeDeferred) = ResolveStates(states);
        var errors = new List<string>();
        long succeeded = 0;
        long failed = 0;

        // Discover sessions + matching messages via peek.
        await using var peekReceiver = _client.CreateReceiver(endpointName, subscriptionName);
        var sessionMessages = new Dictionary<string, List<(long SequenceNumber, ServiceBusMessageState State)>>(StringComparer.Ordinal);
        long fromSequenceNumber = 0;

        while (true)
        {
            var peeked = await peekReceiver.PeekMessagesAsync(100, fromSequenceNumber, cancellationToken).ConfigureAwait(false);
            if (peeked.Count == 0) break;

            foreach (var msg in peeked)
            {
                var stateMatch = (purgeActive && msg.State == ServiceBusMessageState.Active)
                              || (purgeDeferred && msg.State == ServiceBusMessageState.Deferred);

                if (stateMatch && (!enqueuedBeforeUtc.HasValue || msg.EnqueuedTime.UtcDateTime < enqueuedBeforeUtc.Value))
                {
                    var sid = msg.SessionId ?? string.Empty;
                    if (!sessionMessages.TryGetValue(sid, out var list))
                    {
                        list = new List<(long, ServiceBusMessageState)>();
                        sessionMessages[sid] = list;
                    }
                    list.Add((msg.SequenceNumber, msg.State));
                }
            }

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        foreach (var (sid, messages) in sessionMessages)
        {
            ServiceBusSessionReceiver sessionReceiver;
            try
            {
                sessionReceiver = await _client.AcceptSessionAsync(endpointName, subscriptionName, sid, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceBusException ex)
            {
                failed += messages.Count;
                errors.Add($"Session '{sid}': {ex.Message}");
                continue;
            }

            try
            {
                foreach (var seqNum in messages.Where(m => m.State == ServiceBusMessageState.Deferred).Select(m => m.SequenceNumber))
                {
                    try
                    {
                        var msg = await sessionReceiver.ReceiveDeferredMessageAsync(seqNum, cancellationToken).ConfigureAwait(false);
                        if (msg is not null)
                        {
                            await sessionReceiver.CompleteMessageAsync(msg, cancellationToken).ConfigureAwait(false);
                            succeeded++;
                        }
                    }
                    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound)
                    {
                        // already gone — not an error
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"Deferred seq {seqNum}: {ex.Message}");
                    }
                }

                if (messages.Any(m => m.State == ServiceBusMessageState.Active))
                {
                    while (true)
                    {
                        var received = await sessionReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        if (received.Count == 0) break;

                        var anyCompleted = false;
                        foreach (var msg in received)
                        {
                            if (!enqueuedBeforeUtc.HasValue || msg.EnqueuedTime.UtcDateTime < enqueuedBeforeUtc.Value)
                            {
                                await sessionReceiver.CompleteMessageAsync(msg, cancellationToken).ConfigureAwait(false);
                                succeeded++;
                                anyCompleted = true;
                            }
                            else
                            {
                                await sessionReceiver.AbandonMessageAsync(msg, cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                        }
                        if (!anyCompleted) break;
                    }
                }
            }
            finally
            {
                await sessionReceiver.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new TransportBulkResult(
            Processed: succeeded + failed,
            Succeeded: succeeded,
            Failed: failed,
            Errors: errors);
    }

    public async Task<TransportReprocessResult> ReprocessDeferredAsync(string endpointName, string sessionId, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var sessionStateCleared = false;
        var processRequestSent = false;

        try
        {
            var receiver = await _client.AcceptSessionAsync(endpointName, endpointName, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (receiver)
            {
                var stateBytes = await receiver.GetSessionStateAsync(cancellationToken).ConfigureAwait(false);
                if (stateBytes is not null)
                {
                    await receiver.SetSessionStateAsync(null, cancellationToken).ConfigureAwait(false);
                }
                sessionStateCleared = true;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            sessionStateCleared = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clear session state for {SessionId} on {EndpointId}", sessionId, endpointName);
            errors.Add($"Clear session state: {ex.Message}");
        }

        try
        {
            var processRequest = MessageHelper.ToServiceBusMessage(new Message
            {
                To = CoreConstants.DeferredProcessorId,
                SessionId = sessionId,
                MessageType = MessageType.ProcessDeferredRequest,
                MessageContent = new MessageContent(),
                CorrelationId = Guid.NewGuid().ToString(),
            });

            await using var sender = _client.CreateSender(endpointName);
            await sender.SendMessageAsync(processRequest, cancellationToken).ConfigureAwait(false);
            processRequestSent = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send ProcessDeferredRequest for {SessionId} on {EndpointId}", sessionId, endpointName);
            errors.Add($"Send ProcessDeferredRequest: {ex.Message}");
        }

        return new TransportReprocessResult(
            SessionId: sessionId,
            SessionStateCleared: sessionStateCleared,
            ProcessRequestSent: processRequestSent,
            Errors: errors);
    }

    private static (bool active, bool deferred) ResolveStates(IReadOnlyCollection<TransportMessageState> states)
    {
        if (states is null || states.Count == 0)
            return (true, true);
        return (states.Contains(TransportMessageState.Active), states.Contains(TransportMessageState.Deferred));
    }
}
