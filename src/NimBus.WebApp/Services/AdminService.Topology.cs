using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBus.Provisioning;
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

        // Delete deprecated rules first. Rule deletions are independent per
        // (subscription, rule) — parallelize with a small concurrency cap so
        // the Service Bus admin API doesn't rate-limit on busy topics.
        var deprecatedRules = actualTopic.Subscriptions
            .SelectMany(s => s.Rules.Select(r => new { Subscription = s.Name, Rule = r }))
            .Where(x => x.Rule.IsDeprecated)
            .ToList();

        const int RuleDeleteConcurrency = 5;
        var deletedRules = new ConcurrentBag<string>();
        var ruleErrors = new ConcurrentBag<string>();
        using (var gate = new SemaphoreSlim(RuleDeleteConcurrency))
        {
            await Task.WhenAll(deprecatedRules.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    await _sbAdmin.DeleteRuleAsync(endpointNameLower, item.Subscription, item.Rule.Name);
                    deletedRules.Add($"{item.Subscription}/{item.Rule.Name}");
                }
                catch (Exception ex)
                {
                    LogDeleteRuleFailed(ex, item.Rule.Name, item.Subscription);
                    ruleErrors.Add($"Rule {item.Subscription}/{item.Rule.Name}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        result.DeletedRules.AddRange(deletedRules);
        result.Errors.AddRange(ruleErrors);

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
                LogDeleteSubscriptionFailed(ex, sub.Name);
                result.Errors.Add($"Subscription {sub.Name}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// The expected Service Bus topology for an endpoint, taken from
    /// <see cref="TopologyDescriptor"/> — the same declaration
    /// <c>ServiceBusTopologyProvisioner</c> lays down and the subscription admin rebuilds
    /// from.
    /// </summary>
    /// <remarks>
    /// This used to derive the topology a second time from the compiled event catalog,
    /// which is how the <c>{endpoint}-reply</c> subscription came to be reported
    /// deprecated: the provisioner creates it for request/reply, the list here never
    /// mentioned it, and <see cref="RemoveDeprecatedTopologyAsync"/> deleted it — breaking
    /// request/reply for that endpoint until the next topology apply.
    ///
    /// <see cref="GetActualTopology"/> lowercases everything it reads back from the broker,
    /// so the descriptor's names are lowercased to match.
    /// </remarks>
    private TopologySnapshot BuildExpectedTopology(string endpointName)
    {
        var endpoint = _platform.Endpoints
            .FirstOrDefault(x => x.Name.Equals(endpointName, StringComparison.OrdinalIgnoreCase));

        if (endpoint == null)
            return new TopologySnapshot { Name = endpointName, Subscriptions = new List<SubscriptionSnapshot>() };

        return new TopologySnapshot
        {
            Name = endpointName,
            Subscriptions = TopologyDescriptor.ForEndpointTopic(endpoint, _platform)
                .Select(subscription =>
                {
                    var subscriptionName = subscription.Name.ToLowerInvariant();
                    return new SubscriptionSnapshot
                    {
                        Name = subscriptionName,
                        Rules = subscription.Rules
                            .Select(rule => new RuleSnapshot
                            {
                                Name = rule.Name.ToLowerInvariant(),
                                SubscriptionName = subscriptionName,
                            })
                            .ToList(),
                    };
                })
                .ToList(),
        };
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
