using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class ServiceBusTopologyProvisionerTests
{
    [Fact]
    public async Task ApplyAsync_CreatesSessionEnabledDeferredSubscriptionsAndExpectedRules()
    {
        var client = new RecordingAdministrationClient();
        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == "Deferred" && x.RequiresSession);
        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == "DeferredProcessor" && x.RequiresSession);

        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == "orders" && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.ResolverId && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName && x.RuleName == "$Default");
        Assert.Contains(client.DeletedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId && x.RuleName == "$Default");

        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == "orders" && x.Rule.Name == "to-orders");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.ResolverId && x.Rule.Name == "from-orders");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName && x.Rule.Name == "DeferredFilter");
        Assert.Contains(client.CreatedRules, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId && x.Rule.Name == "DeferredProcessorFilter");
    }

    [Fact]
    public async Task ApplyAsync_RecreatesDeferredSubscriptionsWhenExistingOnesDoNotRequireSessions()
    {
        var client = new RecordingAdministrationClient();
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: false));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredProcessorId,
            requiresSession: false));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId);

        var recreatedDeferred = Assert.Single(client.CreatedSubscriptions.Where(x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName));
        var recreatedProcessor = Assert.Single(client.CreatedSubscriptions.Where(x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId));

        Assert.True(recreatedDeferred.RequiresSession);
        Assert.True(recreatedProcessor.RequiresSession);
    }

    [Fact]
    public async Task ApplyAsync_WithMatchingTopology_DoesNotRecreateSubscriptionsOrRules()
    {
        var client = new RecordingAdministrationClient();
        client.SeedTopic(Constants.ResolverId);
        client.SeedTopic("orders");

        client.SeedSubscription(Constants.ResolverId, MakeSubscriptionProperties(Constants.ResolverId, Constants.ResolverId,
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", "orders",
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.ResolverId,
            requiresSession: false, forwardTo: Constants.ResolverId));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: true));
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredProcessorId,
            requiresSession: true));

        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("to-orders", new SqlRuleFilter("user.To = 'orders'")));
        client.SeedRule("orders", Constants.ResolverId, ServiceBusModelFactory.RuleProperties("from-orders", new SqlRuleFilter($"user.To = '{Constants.ResolverId}'"),
            new SqlRuleAction("SET user.From = 'orders'")));
        client.SeedRule("orders", Constants.ResolverId, ServiceBusModelFactory.RuleProperties("to-orders", new SqlRuleFilter("user.To = 'orders'")));
        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("continuation", new SqlRuleFilter($"user.To = '{Constants.ContinuationId}'"),
            new SqlRuleAction($"SET user.To = 'orders'; SET user.From = '{Constants.ContinuationId}'")));
        client.SeedRule("orders", "orders", ServiceBusModelFactory.RuleProperties("retry", new SqlRuleFilter($"user.To = '{Constants.RetryId}'"),
            new SqlRuleAction($"SET user.To = 'orders'; SET user.From = '{Constants.RetryId}'")));
        client.SeedRule("orders", Constants.DeferredSubscriptionName, ServiceBusModelFactory.RuleProperties("DeferredFilter", new SqlRuleFilter("user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL")));
        client.SeedRule("orders", Constants.DeferredProcessorId, ServiceBusModelFactory.RuleProperties("DeferredProcessorFilter", new SqlRuleFilter("user.To = 'DeferredProcessor'")));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Empty(client.CreatedTopics);
        Assert.Empty(client.CreatedSubscriptions);
        Assert.Empty(client.DeletedSubscriptions);
        Assert.Empty(client.CreatedRules);
        Assert.Empty(client.DeletedRules);
    }

    private static SubscriptionProperties MakeSubscriptionProperties(
        string topicName, string subscriptionName,
        bool requiresSession = false, string? forwardTo = null) =>
        ServiceBusModelFactory.SubscriptionProperties(
            topicName, subscriptionName,
            lockDuration: TimeSpan.FromMinutes(1),
            requiresSession: requiresSession,
            defaultMessageTimeToLive: TimeSpan.MaxValue,
            autoDeleteOnIdle: TimeSpan.MaxValue,
            deadLetteringOnMessageExpiration: false,
            maxDeliveryCount: 10,
            enableBatchedOperations: true,
            status: EntityStatus.Active,
            forwardTo: forwardTo,
            forwardDeadLetteredMessagesTo: string.Empty,
            userMetadata: string.Empty);

    private static ServiceBusTopologyProvisioner CreateProvisioner(RecordingAdministrationClient client, IPlatform platform) =>
        new(
            new AzureCliRunner(),
            static (options, cancellationToken, runner) => Task.FromResult("Endpoint=sb://example/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test"),
            _ => client,
            () => platform);

    private sealed class RecordingAdministrationClient : ServiceBusAdministrationClient
    {
        private readonly Dictionary<(string TopicName, string SubscriptionName), SubscriptionProperties> _subscriptions = new();
        private readonly Dictionary<(string TopicName, string SubscriptionName, string RuleName), RuleProperties> _rules = new();
        private readonly HashSet<string> _topics = new(StringComparer.Ordinal);

        public List<CreateSubscriptionOptions> CreatedSubscriptions { get; } = new();
        public List<(string TopicName, string SubscriptionName)> DeletedSubscriptions { get; } = new();
        public List<(string TopicName, string SubscriptionName, CreateRuleOptions Rule)> CreatedRules { get; } = new();
        public List<(string TopicName, string SubscriptionName, string RuleName)> DeletedRules { get; } = new();
        public List<string> CreatedTopics { get; } = new();

        public void SeedTopic(string topicName) => _topics.Add(topicName);

        public void SeedSubscription(string topicName, SubscriptionProperties properties) =>
            _subscriptions[(topicName, properties.SubscriptionName)] = properties;

        public void SeedRule(string topicName, string subscriptionName, RuleProperties properties) =>
            _rules[(topicName, subscriptionName, properties.Name)] = properties;

        public override Task<Response<bool>> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(_topics.Contains(topicName), FakeResponse.Instance));

        public override Task<Response<TopicProperties>> CreateTopicAsync(CreateTopicOptions options, CancellationToken cancellationToken = default)
        {
            _topics.Add(options.Name);
            CreatedTopics.Add(options.Name);
            var topic = ServiceBusModelFactory.TopicProperties(options.Name,
                defaultMessageTimeToLive: options.DefaultMessageTimeToLive,
                autoDeleteOnIdle: options.AutoDeleteOnIdle,
                duplicateDetectionHistoryTimeWindow: options.DuplicateDetectionHistoryTimeWindow,
                maxSizeInMegabytes: options.MaxSizeInMegabytes);
            return Task.FromResult(Response.FromValue(topic, FakeResponse.Instance));
        }

        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            if (_subscriptions.TryGetValue((topicName, subscriptionName), out var subscription))
            {
                return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
            }

            throw new RequestFailedException(404, "Not found");
        }

        public override Task<Response<SubscriptionProperties>> CreateSubscriptionAsync(CreateSubscriptionOptions options, CancellationToken cancellationToken = default)
        {
            CreatedSubscriptions.Add(options);
            var subscription = MakeSubscriptionProperties(
                options.TopicName,
                options.SubscriptionName,
                requiresSession: options.RequiresSession,
                forwardTo: options.ForwardTo);
            _subscriptions[(options.TopicName, options.SubscriptionName)] = subscription;
            // Azure Service Bus auto-creates a $Default rule on new subscriptions
            _rules[(options.TopicName, options.SubscriptionName, "$Default")] =
                ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter());
            return Task.FromResult(Response.FromValue(subscription, FakeResponse.Instance));
        }

        public override Task<Response> DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            DeletedSubscriptions.Add((topicName, subscriptionName));
            _subscriptions.Remove((topicName, subscriptionName));
            return Task.FromResult<Response>(FakeResponse.Instance);
        }

        public override Task<Response<RuleProperties>> GetRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            if (_rules.TryGetValue((topicName, subscriptionName, ruleName), out var rule))
            {
                return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
            }

            throw new RequestFailedException(404, "Not found");
        }

        public override Task<Response<RuleProperties>> CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions ruleOptions, CancellationToken cancellationToken = default)
        {
            CreatedRules.Add((topicName, subscriptionName, ruleOptions));
            var rule = ServiceBusModelFactory.RuleProperties(ruleOptions.Name, ruleOptions.Filter, ruleOptions.Action);
            _rules[(topicName, subscriptionName, ruleOptions.Name)] = rule;
            return Task.FromResult(Response.FromValue(rule, FakeResponse.Instance));
        }

        public override Task<Response> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            DeletedRules.Add((topicName, subscriptionName, ruleName));
            _rules.Remove((topicName, subscriptionName, ruleName));
            return Task.FromResult<Response>(FakeResponse.Instance);
        }
    }

    private sealed class FakeResponse : Response
    {
        public static FakeResponse Instance { get; } = new();

        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            yield break;
        }

        protected override bool TryGetHeader(string name, out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class TestEndpoint : IEndpoint
    {
        public TestEndpoint(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}



