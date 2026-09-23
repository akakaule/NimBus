using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using MessageType = NimBus.Core.Messages.MessageType;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Services;

/// <summary>Read-only broker and stored-history inspection for deferred tracking recovery.</summary>
public sealed class DeferredMessageInspector(ServiceBusClient client, ServiceBusAdministrationClient? administrationClient = null)
{
    private const int ScanLimit = 2000;

    /// <summary>Opaque version preserves timestamp precision through JavaScript clients.</summary>
    public static string RowVersion(UnresolvedEvent row) =>
        $"{row.SessionId}:{row.LastMessageId}:{row.UpdatedAt.Ticks}";

    /// <summary>Inspects the row without locking or settling any broker message.</summary>
    public async Task<DeferredInspection> InspectAsync(UnresolvedEvent row, IEnumerable<MessageEntity> history,
        CancellationToken cancellationToken = default)
    {
        var scoped = history.Where(m => m.EventId == row.EventId && m.SessionId == row.SessionId
            && (string.Equals(m.From, row.EndpointId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.To, row.EndpointId, StringComparison.OrdinalIgnoreCase))).ToArray();
        var terminal = scoped.Where(m => string.Equals(m.From, row.EndpointId, StringComparison.OrdinalIgnoreCase)
            && m.MessageType is MessageType.ResolutionResponse or MessageType.SkipResponse or MessageType.ErrorResponse)
            .OrderByDescending(m => m.EnqueuedTimeUtc)
            // Fail conservatively if timestamps tie: never prefer success to failure.
            .ThenBy(m => m.MessageType == MessageType.ResolutionResponse ? 1 : 0).FirstOrDefault();
        var outcome = terminal is null ? "Unknown"
            : !string.IsNullOrWhiteSpace(terminal.DeadLetterReason) || !string.IsNullOrWhiteSpace(terminal.DeadLetterErrorDescription) ? "DeadLettered"
            : terminal.MessageType == MessageType.ResolutionResponse ? "Completed"
            : terminal.MessageType == MessageType.SkipResponse ? "Skipped" : "Failed";
        var laterAttempt = terminal is not null && scoped.Any(m => m.EnqueuedTimeUtc >= terminal.EnqueuedTimeUtc
            && m.MessageType is MessageType.EventRequest or MessageType.ResubmissionRequest or MessageType.RetryRequest
                or MessageType.ContinuationRequest or MessageType.ProcessDeferredRequest or MessageType.DeferralResponse
                or MessageType.PendingHandoffResponse);
        var result = new DeferredInspection
        {
            RowVersion = RowVersion(row), ResolutionStatus = row.ResolutionStatus.ToString(),
            HistoryOutcome = outcome, TerminalMessageId = terminal?.MessageId, TerminalTime = terminal?.EnqueuedTimeUtc,
            HasLaterAttempt = laterAttempt,
            HistoryDetail = terminal is null
                ? "No recorded terminal outcome for this endpoint and session. Missing broker messages do not prove successful processing."
                : $"Stored history records {outcome} for this endpoint and session."
                    + (laterAttempt ? " A later attempt or deferral exists; this does not prove that attempt completed." : ""),
            BrokerChecks = new List<DeferredBrokerCheck>(),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        foreach (var subscription in new[] { NimBus.Core.Messages.Constants.DeferredSubscriptionName, row.EndpointId })
        {
            foreach (var subQueue in new[] { SubQueue.None, SubQueue.DeadLetter, SubQueue.TransferDeadLetter })
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.BrokerChecks.Add(await PeekAsync(row, subscription, subQueue, timeout.Token, cancellationToken));
            }
        }

        result.CanSkip = row.ResolutionStatus == ResolutionStatus.Deferred
            && row.UpdatedAt <= DateTime.UtcNow.AddMinutes(-15)
            && result.BrokerChecks.All(c => c.Status == DeferredBrokerCheckStatus.NotFound);
        result.SkipDetail = row.ResolutionStatus != ResolutionStatus.Deferred ? "The tracking record is no longer Deferred."
            : row.UpdatedAt > DateTime.UtcNow.AddMinutes(-15) ? "Wait until the record has been unchanged for 15 minutes before skipping."
            : result.BrokerChecks.Any(c => c.Status == DeferredBrokerCheckStatus.Present) ? "A matching broker message still exists. Resolve it before skipping the tracking record."
            : result.BrokerChecks.Any(c => c.Status == DeferredBrokerCheckStatus.Unknown) ? "Broker inspection is incomplete. Skipping is unavailable; try checking again."
            : "No matching broker messages were found. You can skip this stale tracking record after verifying its outcome.";
        return result;
    }

    private async Task<DeferredBrokerCheck> PeekAsync(UnresolvedEvent row, string subscription, SubQueue subQueue,
        CancellationToken timeout, CancellationToken requestCancellation)
    {
        var result = new DeferredBrokerCheck
        {
            Location = subQueue == SubQueue.None ? subscription : $"{subscription}/{subQueue}",
            Status = DeferredBrokerCheckStatus.Unknown,
        };
        try
        {
            timeout.ThrowIfCancellationRequested();
            if (subQueue == SubQueue.TransferDeadLetter && administrationClient is not null)
            {
                // Some emulators expose transfer queue counts but cannot peek these
                // queues over AMQP. Only an explicit zero establishes absence.
                var runtime = await administrationClient.GetSubscriptionRuntimePropertiesAsync(
                    row.EndpointId, subscription, timeout);
                if (runtime.Value.TransferDeadLetterMessageCount == 0)
                {
                    result.Status = DeferredBrokerCheckStatus.NotFound;
                    result.Detail = "Administration API reports this transfer dead-letter queue is empty.";
                    return result;
                }
            }
            await using var receiver = client.CreateReceiver(row.EndpointId, subscription,
                new ServiceBusReceiverOptions { SubQueue = subQueue, PrefetchCount = 0 });
            long next = 0;
            while (result.Scanned < ScanLimit)
            {
                var messages = await receiver.PeekMessagesAsync(Math.Min(100, ScanLimit - result.Scanned), next, timeout);
                if (messages.Count == 0)
                {
                    result.Status = DeferredBrokerCheckStatus.NotFound;
                    result.Detail = "No match in this point-in-time scan.";
                    return result;
                }

                result.Scanned += messages.Count;
                if (messages.Any(m => Matches(m, row)))
                {
                    result.Status = DeferredBrokerCheckStatus.Present;
                    result.Detail = "Matching event and session found.";
                    return result;
                }

                var last = messages.Max(m => m.SequenceNumber);
                if (last < next || last == long.MaxValue) break;
                next = last + 1;
            }
            result.Detail = "Scan limit reached; absence could not be established.";
        }
        catch (Exception exception) when (!requestCancellation.IsCancellationRequested
            && exception is ServiceBusException or Azure.RequestFailedException or OperationCanceledException or UnauthorizedAccessException)
        {
            result.Status = DeferredBrokerCheckStatus.Unknown;
            result.Detail = "Broker could not be inspected. Check connectivity and permissions, then retry.";
        }
        return result;
    }

    private static bool Matches(ServiceBusReceivedMessage message, UnresolvedEvent row)
    {
        var session = message.SessionId;
        if (string.IsNullOrEmpty(session) && message.ApplicationProperties.TryGetValue("OriginalSessionId", out var original))
            session = original as string;
        return string.Equals(session, row.SessionId, StringComparison.Ordinal)
            && message.ApplicationProperties.TryGetValue("EventId", out var eventId)
            && string.Equals(eventId as string, row.EventId, StringComparison.Ordinal);
    }
}
