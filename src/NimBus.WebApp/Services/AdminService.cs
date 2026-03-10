using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using Microsoft.Extensions.Logging;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Services;

public class AdminService : IAdminService
{
    private readonly IPlatform _platform;
    private readonly ICosmosDbClient _cosmosClient;
    private readonly ServiceBusAdministrationClient _sbAdmin;
    private readonly ServiceBusClient _sbClient;
    private readonly IManagerClient _managerClient;
    private readonly ILogger<AdminService> _logger;

    private const int PageSize = 20;
    private const int AgeThresholdMinutes = 10;

    public AdminService(
        IPlatform platform,
        ICosmosDbClient cosmosClient,
        ServiceBusAdministrationClient sbAdmin,
        ServiceBusClient sbClient,
        IManagerClient managerClient,
        ILogger<AdminService> logger)
    {
        _platform = platform;
        _cosmosClient = cosmosClient;
        _sbAdmin = sbAdmin;
        _sbClient = sbClient;
        _managerClient = managerClient;
        _logger = logger;
    }

    // ───────────────────────── Platform Configuration ─────────────────────────

    public Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform)
    {
        var config = new PlatformConfig
        {
            ResolverId = CoreConstants.ResolverId,
            ManagerId = CoreConstants.ManagerId,
            ContinuationId = CoreConstants.ContinuationId,
            EventId = CoreConstants.EventId,
            RetryId = CoreConstants.RetryId,
            Endpoints = platform.Endpoints.Select(ep => new PlatformEndpoint
            {
                Id = ep.Id,
                Name = ep.Name,
                EventTypesProduced = ep.EventTypesProduced.Select(et => new PlatformEventType
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList(),
                EventTypesConsumed = ep.EventTypesConsumed.Select(et => new PlatformEventType
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList()
            }).ToList()
        };

        return Task.FromResult(config);
    }

    // ───────────────────────── Topology Audit ─────────────────────────

    public async Task<TopologyAuditResult> AuditTopologyAsync(string endpointName)
    {
        var endpointNameLower = endpointName.ToLowerInvariant();

        var expectedTopic = BuildExpectedTopology(endpointNameLower);
        var actualTopic = await GetActualTopology(endpointNameLower);
        MarkDeprecated(expectedTopic, actualTopic);

        var hasDeprecated = actualTopic.Subscriptions.Any(s => s.IsDeprecated)
                         || actualTopic.Subscriptions.SelectMany(s => s.Rules).Any(r => r.IsDeprecated);

        return new TopologyAuditResult
        {
            TopicName = endpointNameLower,
            HasDeprecated = hasDeprecated,
            Subscriptions = actualTopic.Subscriptions.Select(s => new SubscriptionTopology
            {
                Name = s.Name,
                IsDeprecated = s.IsDeprecated,
                Rules = s.Rules.Select(r => new RuleTopology
                {
                    Name = r.Name,
                    SubscriptionName = s.Name,
                    IsDeprecated = r.IsDeprecated
                }).ToList()
            }).ToList()
        };
    }

    public async Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName)
    {
        var endpointNameLower = endpointName.ToLowerInvariant();
        var result = new TopologyCleanupResult
        {
            DeletedSubscriptions = new List<string>(),
            DeletedRules = new List<string>(),
            Errors = new List<string>()
        };

        var expectedTopic = BuildExpectedTopology(endpointNameLower);
        var actualTopic = await GetActualTopology(endpointNameLower);
        MarkDeprecated(expectedTopic, actualTopic);

        // Delete deprecated rules first
        var deprecatedRules = actualTopic.Subscriptions
            .SelectMany(s => s.Rules.Select(r => new { Subscription = s.Name, Rule = r }))
            .Where(x => x.Rule.IsDeprecated)
            .ToList();

        foreach (var item in deprecatedRules)
        {
            try
            {
                await _sbAdmin.DeleteRuleAsync(endpointNameLower, item.Subscription, item.Rule.Name);
                result.DeletedRules.Add($"{item.Subscription}/{item.Rule.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete rule {Rule} on subscription {Subscription}",
                    item.Rule.Name, item.Subscription);
                result.Errors.Add($"Rule {item.Subscription}/{item.Rule.Name}: {ex.Message}");
            }
        }

        // Delete deprecated subscriptions
        var deprecatedSubscriptions = actualTopic.Subscriptions
            .Where(s => s.IsDeprecated)
            .ToList();

        foreach (var sub in deprecatedSubscriptions)
        {
            try
            {
                await _sbAdmin.DeleteSubscriptionAsync(endpointNameLower, sub.Name);
                result.DeletedSubscriptions.Add(sub.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete subscription {Subscription}", sub.Name);
                result.Errors.Add($"Subscription {sub.Name}: {ex.Message}");
            }
        }

        return result;
    }

    // ───────────────────────── Bulk Resubmit ─────────────────────────

    public async Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-AgeThresholdMinutes);
        int totalFailed = 0;
        int eligible = 0;
        string continuationToken = string.Empty;

        do
        {
            var response = await _cosmosClient.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.Failed.ToString() }
                },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                totalFailed++;
                if (ev.UpdatedAt < cutoff)
                    eligible++;
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkResubmitPreview
        {
            TotalFailedCount = totalFailed,
            EligibleCount = eligible,
            AgeThresholdMinutes = AgeThresholdMinutes
        };
    }

    public async Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-AgeThresholdMinutes);
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
        string continuationToken = string.Empty;

        do
        {
            var response = await _cosmosClient.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.Failed.ToString() }
                },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                if (ev.UpdatedAt >= cutoff)
                    continue;

                processed++;

                try
                {
                    var failedMessage = await _cosmosClient.GetFailedMessage(ev.EventId, endpointId);
                    if (failedMessage == null)
                    {
                        _logger.LogWarning("Failed message not found for event {EventId} on {EndpointId}, skipping",
                            ev.EventId, endpointId);
                        failed++;
                        errors.Add($"{ev.EventId}: failed message not found");
                        continue;
                    }

                    var eventTypeId = failedMessage.EventTypeId;
                    var eventJson = failedMessage.MessageContent?.EventContent?.EventJson;

                    if (string.IsNullOrEmpty(eventJson))
                    {
                        _logger.LogWarning("No event content for event {EventId}, skipping", ev.EventId);
                        failed++;
                        errors.Add($"{ev.EventId}: no event content");
                        continue;
                    }

                    await _managerClient.Resubmit(failedMessage, endpointId, eventTypeId, eventJson);
                    await _cosmosClient.ArchiveFailedEvent(ev.EventId, ev.SessionId, endpointId);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resubmit event {EventId}", ev.EventId);
                    failed++;
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult
        {
            Processed = processed,
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors
        };
    }

    // ───────────────────────── Dead-Lettered ─────────────────────────

    public async Task<int> GetDeadLetteredCountAsync(string endpointId)
    {
        var stateCount = await _cosmosClient.DownloadEndpointStateCount(endpointId);
        return stateCount.DeadletterCount;
    }

    public async Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId)
    {
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
        string continuationToken = string.Empty;

        do
        {
            var response = await _cosmosClient.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.DeadLettered.ToString() }
                },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                processed++;

                try
                {
                    bool removed = await _cosmosClient.RemoveMessage(ev.EventId, ev.SessionId, endpointId);
                    if (removed)
                        succeeded++;
                    else
                    {
                        failed++;
                        errors.Add($"{ev.EventId}: remove returned false");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete dead-lettered event {EventId}", ev.EventId);
                    failed++;
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult
        {
            Processed = processed,
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors
        };
    }

    // ───────────────────────── Session Management ─────────────────────────

    public async Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId)
    {
        var sessionState = await _cosmosClient.DownloadEndpointSessionStateCount(endpointId, sessionId);

        // Count Cosmos events for the session
        int cosmosEventCount = 0;
        string continuationToken = string.Empty;

        do
        {
            var response = await _cosmosClient.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, SessionId = sessionId },
                continuationToken, PageSize);

            cosmosEventCount += response.Events.Count();
            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        // Peek Deferred subscription
        int deferredSubscriptionCount = 0;
        try
        {
            var deferredReceiver = await _sbClient.AcceptSessionAsync(
                endpointId, CoreConstants.DeferredSubscriptionName, sessionId);
            await using (deferredReceiver)
            {
                var peeked = await deferredReceiver.PeekMessagesAsync(100);
                deferredSubscriptionCount = peeked.Count;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No session — 0 messages
        }

        return new SessionPurgePreview
        {
            SessionId = sessionId,
            PendingCount = sessionState.PendingEvents.Count(),
            DeferredCount = sessionState.DeferredEvents.Count(),
            DeferredSubscriptionCount = deferredSubscriptionCount,
            CosmosEventCount = cosmosEventCount
        };
    }

    public async Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId)
    {
        var result = new SessionPurgeResult
        {
            SessionId = sessionId,
            Errors = new List<string>()
        };

        ServiceBusSessionReceiver receiver = null;
        try
        {
            receiver = await _sbClient.AcceptSessionAsync(endpointId, endpointId, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not accept SB session {SessionId} on {EndpointId}", sessionId, endpointId);
            result.Errors.Add($"Could not accept session: {ex.Message}");
        }

        if (receiver != null)
        {
            // Remove active messages
            try
            {
                int activeRemoved = 0;
                int batchCount;
                do
                {
                    var messages = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                    batchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        try
                        {
                            await receiver.CompleteMessageAsync(message);
                            activeRemoved++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to complete active message {MessageId}", message.MessageId);
                            result.Errors.Add($"Active message {message.MessageId}: {ex.Message}");
                        }
                    }
                }
                while (batchCount > 0);

                result.ActiveMessagesRemoved = activeRemoved;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error removing active messages from session {SessionId}", sessionId);
                result.Errors.Add($"Active messages error: {ex.Message}");
            }

            // Remove deferred messages
            try
            {
                int deferredRemoved = 0;
                long? fromSequenceNumber = null;
                int peekCount;
                do
                {
                    var peekedMessages = fromSequenceNumber.HasValue
                        ? await receiver.PeekMessagesAsync(100, fromSequenceNumber.Value)
                        : await receiver.PeekMessagesAsync(100);
                    peekCount = peekedMessages.Count;
                    if (peekCount > 0)
                        fromSequenceNumber = peekedMessages[peekCount - 1].SequenceNumber + 1;
                    foreach (var message in peekedMessages)
                    {
                        if (message.State == ServiceBusMessageState.Deferred)
                        {
                            try
                            {
                                var deferredMessage = await receiver.ReceiveDeferredMessageAsync(message.SequenceNumber);
                                if (deferredMessage != null)
                                {
                                    await receiver.CompleteMessageAsync(deferredMessage);
                                    deferredRemoved++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to complete deferred message {SequenceNumber}",
                                    message.SequenceNumber);
                                result.Errors.Add($"Deferred message seq {message.SequenceNumber}: {ex.Message}");
                            }
                        }
                    }
                }
                while (peekCount > 0);

                result.DeferredMessagesRemoved = deferredRemoved;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error removing deferred messages from session {SessionId}", sessionId);
                result.Errors.Add($"Deferred messages error: {ex.Message}");
            }

            // Clear session state
            try
            {
                await receiver.SetSessionStateAsync(null);
                result.SessionStateCleared = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear session state for {SessionId}", sessionId);
                result.Errors.Add($"Clear session state: {ex.Message}");
            }

            await receiver.DisposeAsync();
        }

        // Remove messages from "Deferred" subscription
        try
        {
            var deferredReceiver = await _sbClient.AcceptSessionAsync(
                endpointId, CoreConstants.DeferredSubscriptionName, sessionId);

            await using (deferredReceiver)
            {
                int deferredSubRemoved = 0;
                int deferredBatchCount;
                do
                {
                    var messages = await deferredReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                    deferredBatchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        await deferredReceiver.CompleteMessageAsync(message);
                        deferredSubRemoved++;
                    }
                }
                while (deferredBatchCount > 0);

                result.DeferredSubscriptionMessagesRemoved = deferredSubRemoved;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No deferred messages for this session — not an error
            result.DeferredSubscriptionMessagesRemoved = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing messages from Deferred subscription for session {SessionId}", sessionId);
            result.Errors.Add($"Deferred subscription error: {ex.Message}");
        }

        // Remove Cosmos events for this session
        try
        {
            int cosmosRemoved = 0;
            string continuationToken = string.Empty;

            do
            {
                var response = await _cosmosClient.GetEventsByFilter(
                    new MessageStore.EventFilter { EndPointId = endpointId, SessionId = sessionId },
                    continuationToken, PageSize);

                foreach (var ev in response.Events)
                {
                    try
                    {
                        await _cosmosClient.RemoveMessage(ev.EventId, sessionId, endpointId);
                        cosmosRemoved++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove Cosmos event {EventId}", ev.EventId);
                        result.Errors.Add($"Cosmos event {ev.EventId}: {ex.Message}");
                    }
                }

                continuationToken = response.ContinuationToken;
            }
            while (continuationToken != null);

            result.CosmosEventsRemoved = cosmosRemoved;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing Cosmos events for session {SessionId}", sessionId);
            result.Errors.Add($"Cosmos removal error: {ex.Message}");
        }

        return result;
    }

    // ───────────────────────── Single Event ─────────────────────────

    public async Task<bool> DeleteEventAsync(string endpointId, string eventId)
    {
        var ev = await _cosmosClient.GetEvent(endpointId, eventId);
        if (ev == null)
            return false;

        return await _cosmosClient.RemoveMessage(ev.EventId, ev.SessionId, endpointId);
    }

    // ═══════════════════════════ Private helpers ═══════════════════════════

    /// <summary>
    /// Builds the expected Service Bus topology for an endpoint based on platform configuration.
    /// Mirrors the logic from NimBus.CommandLine/Endpoint.cs GetExpectedTopic.
    /// </summary>
    private TopologySnapshot BuildExpectedTopology(string endpointName)
    {
        var endpoint = _platform.Endpoints
            .FirstOrDefault(x => x.Name.Equals(endpointName, StringComparison.OrdinalIgnoreCase));

        if (endpoint == null)
            return new TopologySnapshot { Name = endpointName, Subscriptions = new List<SubscriptionSnapshot>() };

        var snapshot = new TopologySnapshot
        {
            Name = endpointName,
            Subscriptions = new List<SubscriptionSnapshot>
            {
                // Main subscription
                new SubscriptionSnapshot
                {
                    Name = endpointName,
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = $"to-{endpointName}", SubscriptionName = endpointName }
                    }
                },
                // Resolver subscription
                new SubscriptionSnapshot
                {
                    Name = "resolver",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = $"to-{endpointName}", SubscriptionName = "resolver" },
                        new RuleSnapshot { Name = $"from-{endpointName}", SubscriptionName = "resolver" }
                    }
                },
                // Broker subscription
                new SubscriptionSnapshot
                {
                    Name = "broker",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = $"from-{endpointName}", SubscriptionName = "broker" }
                    }
                },
                // Continuation subscription
                new SubscriptionSnapshot
                {
                    Name = "continuation",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "continuation", SubscriptionName = "continuation" }
                    }
                },
                // Retry subscription
                new SubscriptionSnapshot
                {
                    Name = "retry",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "retry", SubscriptionName = "retry" }
                    }
                },
                // Deferred subscription
                new SubscriptionSnapshot
                {
                    Name = "deferred",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "to-deferred", SubscriptionName = "deferred" }
                    }
                },
                // DeferredProcessor subscription
                new SubscriptionSnapshot
                {
                    Name = "deferredprocessor",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "to-deferredprocessor", SubscriptionName = "deferredprocessor" }
                    }
                }
            }
        };

        // Forward-from-eventtype-to-endpoint subscriptions
        var createdSubscriptions = new List<SubscriptionSnapshot>();
        foreach (var eventType in endpoint.EventTypesProduced)
        {
            var consumers = _platform.Endpoints
                .Where(x => x.EventTypesConsumed.Contains(eventType))
                .ToList();

            foreach (var consumer in consumers)
            {
                var existing = createdSubscriptions
                    .FirstOrDefault(x => x.Name.Equals(consumer.Name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Rules.Add(new RuleSnapshot
                    {
                        Name = eventType.Id.ToLowerInvariant(),
                        SubscriptionName = existing.Name.ToLowerInvariant()
                    });
                }
                else
                {
                    createdSubscriptions.Add(new SubscriptionSnapshot
                    {
                        Name = consumer.Name.ToLowerInvariant(),
                        Rules = new List<RuleSnapshot>
                        {
                            new RuleSnapshot
                            {
                                Name = eventType.Id.ToLowerInvariant(),
                                SubscriptionName = consumer.Name.ToLowerInvariant()
                            }
                        }
                    });
                }
            }
        }

        snapshot.Subscriptions.AddRange(createdSubscriptions);
        return snapshot;
    }

    /// <summary>
    /// Fetches the actual Service Bus topology from the administration client.
    /// Mirrors NimBus.CommandLine/Endpoint.cs GetActualTopic.
    /// </summary>
    private async Task<TopologySnapshot> GetActualTopology(string endpointName)
    {
        var snapshot = new TopologySnapshot
        {
            Name = endpointName,
            Subscriptions = new List<SubscriptionSnapshot>()
        };

        await foreach (var page in _sbAdmin.GetSubscriptionsAsync(endpointName).AsPages())
        {
            var subscriptions = page.Values.Select(x => new SubscriptionSnapshot
            {
                Name = x.SubscriptionName.ToLowerInvariant(),
                Rules = new List<RuleSnapshot>()
            }).ToList();

            snapshot.Subscriptions.AddRange(subscriptions);
        }

        foreach (var subscription in snapshot.Subscriptions)
        {
            await foreach (var page in _sbAdmin.GetRulesAsync(endpointName, subscription.Name).AsPages())
            {
                var rules = page.Values.Select(x => new RuleSnapshot
                {
                    Name = x.Name.ToLowerInvariant(),
                    SubscriptionName = subscription.Name.ToLowerInvariant()
                }).ToList();

                subscription.Rules.AddRange(rules);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Compares expected vs actual topology and marks deprecated items.
    /// Mirrors NimBus.CommandLine/Endpoint.cs GetIsDeprecatedTopic.
    /// </summary>
    private static void MarkDeprecated(TopologySnapshot expected, TopologySnapshot actual)
    {
        var expectedRules = expected.Subscriptions.SelectMany(s => s.Rules).ToList();

        foreach (var subscription in actual.Subscriptions)
        {
            subscription.IsDeprecated = !expected.Subscriptions
                .Any(e => e.Name.Equals(subscription.Name, StringComparison.OrdinalIgnoreCase));

            foreach (var rule in subscription.Rules)
            {
                rule.IsDeprecated = !expectedRules.Any(e =>
                    e.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase) &&
                    e.SubscriptionName.Equals(rule.SubscriptionName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    // ─────────── Internal DTOs for topology comparison ───────────

    private sealed class TopologySnapshot
    {
        public string Name { get; set; } = string.Empty;
        public List<SubscriptionSnapshot> Subscriptions { get; set; } = new List<SubscriptionSnapshot>();
    }

    private sealed class SubscriptionSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public List<RuleSnapshot> Rules { get; set; } = new List<RuleSnapshot>();
        public bool IsDeprecated { get; set; }
    }

    private sealed class RuleSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string SubscriptionName { get; set; } = string.Empty;
        public bool IsDeprecated { get; set; }
    }
}
