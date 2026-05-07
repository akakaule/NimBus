using System.Text.Json;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;

namespace CrmErpDemo.AppHost;

// Generates the Service Bus emulator's UserConfig JSON from an IPlatform.
//
// We use this instead of NimBus.CommandLine.ServiceBusTopologyProvisioner when
// running under the emulator: the provisioner relies on
// ServiceBusAdministrationClient (HTTPS REST) which the 2.0.0 emulator doesn't
// expose on a port the SDK's connection-string-driven URL synthesis can find.
// The emulator does, however, accept a static UserConfig at startup describing
// every topic / subscription / rule. So we build that config from the same
// IPlatform the runtime provisioner consults, write it next to the AppHost
// binary, and hand it to RunAsEmulator(...).WithConfigurationFile(...).
//
// The shape mirrors EnsureEndpointTopologyAsync in
// src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs — the production
// provisioner stays the source of truth for real Azure.
internal static class EmulatorTopologyConfigBuilder
{
    public static string Build(IPlatform platform)
    {
        var endpoints = platform.Endpoints
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var topics = new List<object>
        {
            BuildResolverTopic(),
        };

        foreach (var endpoint in endpoints)
        {
            topics.Add(BuildEndpointTopic(platform, endpoint));
        }

        var root = new
        {
            UserConfig = new
            {
                Namespaces = new[]
                {
                    new
                    {
                        Name = "sbemulatorns",
                        Topics = topics,
                        Queues = Array.Empty<object>(),
                    },
                },
                Logging = new { Type = "File" },
            },
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        });
    }

    private static object BuildResolverTopic() => new
    {
        Name = Constants.ResolverId,
        Properties = TopicProperties(),
        Subscriptions = new[]
        {
            new
            {
                Name = Constants.ResolverId,
                Properties = SessionSubscriptionProperties(forwardTo: null),
                // Resolver subscription keeps the default catch-all rule so every
                // forwarded response message lands here regardless of user.* hints.
                Rules = new[] { DefaultRule() },
            },
        },
    };

    private static object BuildEndpointTopic(IPlatform platform, IEndpoint endpoint)
    {
        var subscriptions = new List<object>
        {
            // Self-subscription — endpoint receives its own routed traffic plus
            // continuation / retry control envelopes addressed to this endpoint.
            new
            {
                Name = endpoint.Id,
                Properties = SessionSubscriptionProperties(forwardTo: null),
                Rules = new object[]
                {
                    Rule($"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null),
                    Rule("continuation", $"user.To = '{Constants.ContinuationId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.ContinuationId}'"),
                    Rule("retry", $"user.To = '{Constants.RetryId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.RetryId}'"),
                },
            },
            // Forwarding to the centralised Resolver — every audit message
            // produced by this endpoint flows through here.
            new
            {
                Name = Constants.ResolverId,
                Properties = ForwardSubscriptionProperties(Constants.ResolverId),
                Rules = new object[]
                {
                    Rule($"from-{endpoint.Id}", $"user.To = '{Constants.ResolverId}'", $"SET user.From = '{endpoint.Id}'"),
                    Rule($"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null),
                },
            },
            // Deferred parking lot for sibling messages while a session is
            // blocked. Session-required so we can replay in FIFO; TTL is
            // clamped to 1 hour for the emulator (real Azure uses 14 days).
            new
            {
                Name = "Deferred",
                Properties = DeferredSubscriptionProperties(),
                Rules = new object[]
                {
                    Rule("DeferredFilter", "user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL", action: null),
                },
            },
            // DeferredProcessor: non-session control queue used by the worker
            // host to drive the deferred replay loop.
            new
            {
                Name = "DeferredProcessor",
                Properties = NonSessionSubscriptionProperties(),
                Rules = new object[]
                {
                    Rule("DeferredProcessorFilter", "user.To = 'DeferredProcessor'", action: null),
                },
            },
        };

        // Cross-endpoint forwarding — for every event-type this endpoint
        // produces, fan out to each consuming endpoint via a forward
        // subscription with one rule per event-type.
        foreach (var consumer in platform.Endpoints
            .Where(c => !string.Equals(c.Id, endpoint.Id, StringComparison.Ordinal))
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var rules = new List<object>();
            foreach (var eventType in endpoint.EventTypesProduced
                .Where(et => platform.GetConsumers(et).Any(c => c.Id == consumer.Id))
                .OrderBy(et => et.Id, StringComparer.Ordinal))
            {
                // The "From IS NULL" guard prevents a forwarding loop when an
                // event type is produced AND consumed by both endpoints — see
                // the matching note in ServiceBusTopologyProvisioner.cs.
                rules.Add(Rule(
                    eventType.Id,
                    $"user.EventTypeId = '{eventType.Id}' AND user.From IS NULL",
                    $"SET user.From = '{endpoint.Id}'; SET user.EventId = newid(); SET user.To = '{consumer.Id}';"));
            }

            if (rules.Count == 0) continue;

            subscriptions.Add(new
            {
                Name = consumer.Id,
                Properties = ForwardSubscriptionProperties(consumer.Id),
                Rules = rules.ToArray(),
            });
        }

        return new
        {
            Name = endpoint.Id,
            Properties = TopicProperties(),
            Subscriptions = subscriptions.ToArray(),
        };
    }

    private static object TopicProperties() => new
    {
        // Emulator caps topic size at 100 MB and the duplicate-detection
        // window at 5 minutes; production uses 10 minutes. We're not relying
        // on duplicate detection in the demo, but the field is required.
        DuplicateDetectionHistoryTimeWindow = "PT5M",
        RequiresDuplicateDetection = false,
        DefaultMessageTimeToLive = "PT1H",
    };

    private static object SessionSubscriptionProperties(string? forwardTo) => new
    {
        DeadLetteringOnMessageExpiration = false,
        DefaultMessageTimeToLive = "PT1H",
        LockDuration = "PT30S",
        MaxDeliveryCount = 10,
        ForwardTo = forwardTo ?? string.Empty,
        ForwardDeadLetteredMessagesTo = string.Empty,
        RequiresSession = true,
    };

    private static object DeferredSubscriptionProperties() => new
    {
        DeadLetteringOnMessageExpiration = false,
        DefaultMessageTimeToLive = "PT1H",
        LockDuration = "PT30S",
        MaxDeliveryCount = 10,
        ForwardTo = string.Empty,
        ForwardDeadLetteredMessagesTo = string.Empty,
        RequiresSession = true,
    };

    private static object NonSessionSubscriptionProperties() => new
    {
        DeadLetteringOnMessageExpiration = false,
        DefaultMessageTimeToLive = "PT1H",
        LockDuration = "PT30S",
        MaxDeliveryCount = 10,
        ForwardTo = string.Empty,
        ForwardDeadLetteredMessagesTo = string.Empty,
        RequiresSession = false,
    };

    private static object ForwardSubscriptionProperties(string forwardTo) => new
    {
        DeadLetteringOnMessageExpiration = false,
        DefaultMessageTimeToLive = "PT1H",
        LockDuration = "PT30S",
        MaxDeliveryCount = 10,
        ForwardTo = forwardTo,
        ForwardDeadLetteredMessagesTo = string.Empty,
        RequiresSession = false,
    };

    private static object Rule(string name, string sqlFilter, string? action)
    {
        // The emulator rejects empty SqlExpression on Action — we have to omit
        // the Action property entirely when the rule has no transformation.
        if (string.IsNullOrEmpty(action))
        {
            return new
            {
                Name = name,
                Properties = new
                {
                    FilterType = "Sql",
                    SqlFilter = new { SqlExpression = sqlFilter },
                },
            };
        }

        return new
        {
            Name = name,
            Properties = new
            {
                FilterType = "Sql",
                SqlFilter = new { SqlExpression = sqlFilter },
                Action = new { SqlExpression = action },
            },
        };
    }

    private static object DefaultRule() => new
    {
        Name = "$Default",
        Properties = new
        {
            FilterType = "Sql",
            SqlFilter = new { SqlExpression = "1=1" },
        },
    };
}
