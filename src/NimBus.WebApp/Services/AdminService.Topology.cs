using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

// Service Bus topology audit + cleanup: compares expected (derived from
// the platform's endpoint catalog) vs actual (queried via the SB admin
// client) and offers a targeted deletion of deprecated subs + rules.
public partial class AdminService
{
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
                // Endpoint subscription — carries the consumer's "to-<endpoint>" rule
                // plus the continuation and retry rules (the provisioner attaches
                // continuation/retry as rules on the endpoint sub, not as separate subs).
                new SubscriptionSnapshot
                {
                    Name = endpointName,
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = $"to-{endpointName}", SubscriptionName = endpointName },
                        new RuleSnapshot { Name = "continuation", SubscriptionName = endpointName },
                        new RuleSnapshot { Name = "retry", SubscriptionName = endpointName }
                    }
                },
                // Resolver subscription — fans out every published event for audit.
                new SubscriptionSnapshot
                {
                    Name = "resolver",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = $"to-{endpointName}", SubscriptionName = "resolver" },
                        new RuleSnapshot { Name = $"from-{endpointName}", SubscriptionName = "resolver" }
                    }
                },
                // Deferred subscription — captures sessions parked behind a failure.
                new SubscriptionSnapshot
                {
                    Name = "deferred",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "deferredfilter", SubscriptionName = "deferred" }
                    }
                },
                // DeferredProcessor subscription — drains parked sessions after resubmit.
                new SubscriptionSnapshot
                {
                    Name = "deferredprocessor",
                    Rules = new List<RuleSnapshot>
                    {
                        new RuleSnapshot { Name = "deferredprocessorfilter", SubscriptionName = "deferredprocessor" }
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
}
