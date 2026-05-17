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
        Assert.Contains(client.CreatedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == "DeferredProcessor" && !x.RequiresSession);

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
    public async Task ApplyAsync_RecreatesDeferredSubscriptionsWhenSessionSupportMismatch()
    {
        var client = new RecordingAdministrationClient();
        // Deferred subscription seeded WITHOUT sessions — should be recreated WITH sessions
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredSubscriptionName,
            requiresSession: false));
        // DeferredProcessor seeded WITH sessions — should be recreated WITHOUT sessions
        client.SeedSubscription("orders", MakeSubscriptionProperties("orders", Constants.DeferredProcessorId,
            requiresSession: true));

        var sut = CreateProvisioner(client, new TestPlatform(new TestEndpoint("orders")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        Assert.Contains(client.DeletedSubscriptions, x => x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId);

        var recreatedDeferred = Assert.Single(client.CreatedSubscriptions, x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredSubscriptionName);
        var recreatedProcessor = Assert.Single(client.CreatedSubscriptions, x =>
            x.TopicName == "orders" && x.SubscriptionName == Constants.DeferredProcessorId);

        Assert.True(recreatedDeferred.RequiresSession);
        Assert.False(recreatedProcessor.RequiresSession);
    }

    [Fact]
    public async Task ApplyAsync_CrossTopicForwardRule_OnlyMatchesOriginalPublishes()
    {
        // Regression for the forwarding-loop bug: when an event type is produced AND
        // consumed by both endpoints (e.g. ContactCreated in CrmErpDemo where CRM and
        // ERP both publish and subscribe), a forward rule that filters only on
        // EventTypeId triggers on its own forwarded output:
        //   CRM publishes -> forwarded to ERP -> ERP's forward sub re-matches the
        //   same EventTypeId -> forwarded back to CRM -> ...
        // Service Bus's MaxHopCount eventually dead-letters the message
        // ("Maximum transfer hop count is exceeded").
        // The fix: filter must include "AND user.From IS NULL" so the rule only fires
        // on original publishes (where the publisher never sets From), not on
        // already-forwarded copies (where the action SETs From=<endpoint>).
        var client = new RecordingAdministrationClient();

        var crm = new EventEndpoint(
            "CrmEndpoint",
            produces: new[] { "ContactCreated" },
            consumes: new[] { "ContactCreated" });
        var erp = new EventEndpoint(
            "ErpEndpoint",
            produces: new[] { "ContactCreated" },
            consumes: new[] { "ContactCreated" });

        var sut = CreateProvisioner(client, new TestPlatform(crm, erp));
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        var crmToErpRule = Assert.Single(client.CreatedRules, r =>
            r.TopicName == "CrmEndpoint" && r.SubscriptionName == "ErpEndpoint" && r.Rule.Name == "ContactCreated");
        var erpToCrmRule = Assert.Single(client.CreatedRules, r =>
            r.TopicName == "ErpEndpoint" && r.SubscriptionName == "CrmEndpoint" && r.Rule.Name == "ContactCreated");

        var crmFilter = ((SqlRuleFilter)crmToErpRule.Rule.Filter).SqlExpression;
        var erpFilter = ((SqlRuleFilter)erpToCrmRule.Rule.Filter).SqlExpression;

        Assert.Contains("user.From IS NULL", crmFilter, StringComparison.Ordinal);
        Assert.Contains("user.From IS NULL", erpFilter, StringComparison.Ordinal);
        Assert.Contains("user.EventTypeId = 'ContactCreated'", crmFilter, StringComparison.Ordinal);
        Assert.Contains("user.EventTypeId = 'ContactCreated'", erpFilter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WithMultipleEventsToSameConsumer_KeepsAllForwardingRules()
    {
        // Regression for the ForwardTo-comparison bug: when a producer endpoint emits
        // multiple events all consumed by the same other endpoint, the provisioner used
        // to call EnsureForwardSubscriptionAsync once per event. Each call after the
        // first detected a "ForwardTo mismatch" (because Azure normalises ForwardTo to
        // a lowercased entity name or full URL while the code passes the PascalCase
        // bare name) and deleted+recreated the subscription, wiping every rule added
        // by previous iterations. End state: only the LAST rule (alphabetically) survived.
        var client = new RecordingAdministrationClient(forwardToNormalizer: forwardTo => forwardTo?.ToLowerInvariant());

        var producer = new EventEndpoint(
            "CrmEndpoint",
            produces: new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" },
            consumes: Array.Empty<string>());
        var consumer = new EventEndpoint(
            "ErpEndpoint",
            produces: Array.Empty<string>(),
            consumes: new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" });

        var sut = CreateProvisioner(client, new TestPlatform(producer, consumer));
        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), CancellationToken.None);

        // The cross-topic forward subscription should be created exactly once and never deleted.
        Assert.Single(client.CreatedSubscriptions, s =>
            s.TopicName == "CrmEndpoint" && s.SubscriptionName == "ErpEndpoint");
        Assert.DoesNotContain(client.DeletedSubscriptions, x =>
            x.TopicName == "CrmEndpoint" && x.SubscriptionName == "ErpEndpoint");

        // All four forwarding rules must coexist — not just the alphabetically-last one.
        foreach (var eventName in new[] { "AccountCreated", "AccountUpdated", "ContactCreated", "ContactUpdated" })
        {
            Assert.Contains(client.CreatedRules, r =>
                r.TopicName == "CrmEndpoint" &&
                r.SubscriptionName == "ErpEndpoint" &&
                r.Rule.Name == eventName);
        }
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
            requiresSession: false));

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
        private readonly Func<string?, string?> _forwardToNormalizer;

        public RecordingAdministrationClient(Func<string?, string?>? forwardToNormalizer = null)
        {
            // Azure Service Bus normalises ForwardTo on read (e.g. lowercases entity
            // names, returns full URLs). The default test client preserves what was
            // sent so most tests don't have to care; specific tests opt in to a
            // normalisation function to model Azure's behaviour.
            _forwardToNormalizer = forwardToNormalizer ?? (forwardTo => forwardTo);
        }

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
                forwardTo: _forwardToNormalizer(options.ForwardTo));
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

    private sealed class EventEndpoint : IEndpoint
    {
        public EventEndpoint(string id, IEnumerable<string> produces, IEnumerable<string> consumes)
        {
            Id = id;
            Name = id;
            EventTypesProduced = produces.Select(name => (IEventType)new TestEventType(name)).ToList();
            EventTypesConsumed = consumes.Select(name => (IEventType)new TestEventType(name)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }

    private sealed class TestEventType : IEventType
    {
        public TestEventType(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Namespace => "Tests";
        public string Description => string.Empty;
        public string SessionKeyProperty => string.Empty;
        public IEnumerable<IProperty> Properties => Array.Empty<IProperty>();
        public Type GetEventClassType() => typeof(TestEventType);
        public IEvent GetEventExample() => null!;

        // Equality keyed on Id so Platform.GetConsumers (which uses
        // EventTypesConsumed.Contains) matches across endpoints that each
        // own their own IEventType instances for the same logical event.
        public override bool Equals(object? obj) =>
            obj is TestEventType other && string.Equals(Id, other.Id, StringComparison.Ordinal);
        public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
    }
}


