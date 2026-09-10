#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public sealed class EndpointManagementTests
{
    private static readonly string[] ExpectedClearCalls =
    {
        "DeleteSubscription|Orders|Orders",
        "CreateSubscription|Orders|Orders",
        "DeleteRule|Orders|Orders|$Default",
        "CreateCustomRule|Orders|Orders|to-Orders|user.To = 'Orders'|<null>",
        "CreateCustomRule|Orders|Orders|continuation|user.To = 'Continuation'|SET user.To = 'Orders'; SET user.From = 'Continuation'",
        "CreateCustomRule|Orders|Orders|retry|user.To = 'Retry'|SET user.To = 'Orders'; SET user.From = 'Retry'",
        "RecreateSubscription|Orders|Deferred",
    };

    [TestMethod]
    public async Task ClearEndpoint_rebuilds_subscription_and_rules_in_provisioner_order()
    {
        var management = new RecordingServiceBusManagement();
        var sut = new EndpointManagement(management);

        await sut.ClearEndpoint("Orders");

        CollectionAssert.AreEqual(ExpectedClearCalls, management.Calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task ClearEndpoint_stops_after_first_management_failure(int failingCall)
    {
        var management = new RecordingServiceBusManagement { FailingCall = failingCall };
        var sut = new EndpointManagement(management);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.ClearEndpoint("Orders"));

        Assert.AreEqual(failingCall + 1, management.Calls.Count);
    }

    [TestMethod]
    public async Task RecreateSubscription_preserves_deferred_settings_and_rules_without_default_match_all()
    {
        var client = new RecreatingAdministrationClient();
        IServiceBusManagement management = new ServiceBusManagement(client);

        await management.RecreateSubscription("Orders", "Deferred");

        CollectionAssert.AreEqual(new[] { "read", "rules", "delete", "create" }, client.Calls);
        var created = client.Created!;
        Assert.AreEqual("Orders", created.TopicName);
        Assert.AreEqual("Deferred", created.SubscriptionName);
        Assert.IsTrue(created.RequiresSession);
        Assert.AreEqual(TimeSpan.FromHours(1), created.DefaultMessageTimeToLive);
        Assert.AreEqual(EntityStatus.ReceiveDisabled, created.Status);
        Assert.AreEqual(7, created.MaxDeliveryCount);
        Assert.AreEqual("DeferredFilter", client.CreatedRule!.Name);
        Assert.AreEqual("user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL",
            ((SqlRuleFilter)client.CreatedRule.Filter).SqlExpression);
    }

    [TestMethod]
    public async Task RecreateSubscription_does_not_delete_when_reading_rules_fails()
    {
        var client = new RecreatingAdministrationClient { FailRules = true };
        IServiceBusManagement management = new ServiceBusManagement(client);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => management.RecreateSubscription("Orders", "Deferred"));

        CollectionAssert.AreEqual(new[] { "read", "rules" }, client.Calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task RecreateSubscription_retains_empty_and_multiple_rule_sets(int ruleCount)
    {
        var client = new RecreatingAdministrationClient { RuleCount = ruleCount };
        IServiceBusManagement management = new ServiceBusManagement(client);

        await management.RecreateSubscription("Orders", "Deferred");

        if (ruleCount == 0)
        {
            Assert.IsInstanceOfType<FalseRuleFilter>(client.CreatedRule!.Filter);
            Assert.AreEqual("delete-rule:$Default", client.Calls[^1]);
        }
        else
        {
            Assert.AreEqual("create-rule:custom", client.Calls[^1]);
            Assert.AreEqual("SET user.Source = 'retained'", ((SqlRuleAction)client.AddedRule!.Action).SqlExpression);
        }
    }

    [TestMethod]
    public async Task RecreateSubscription_propagates_recreation_failure()
    {
        var client = new RecreatingAdministrationClient { FailCreate = true };
        IServiceBusManagement management = new ServiceBusManagement(client);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => management.RecreateSubscription("Orders", "Deferred"));

        Assert.AreEqual("create", client.Calls[^1]);
    }

    private sealed class RecreatingAdministrationClient : ServiceBusAdministrationClient
    {
        public List<string> Calls { get; } = new();
        public bool FailRules { get; init; }
        public bool FailCreate { get; init; }
        public int RuleCount { get; init; } = 1;
        public CreateSubscriptionOptions? Created { get; private set; }
        public CreateRuleOptions? CreatedRule { get; private set; }
        public CreateRuleOptions? AddedRule { get; private set; }

        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Calls.Add("read");
            var properties = ServiceBusModelFactory.SubscriptionProperties(topicName, subscriptionName,
                lockDuration: TimeSpan.FromSeconds(30), requiresSession: true,
                defaultMessageTimeToLive: TimeSpan.FromHours(1), autoDeleteOnIdle: TimeSpan.MaxValue,
                deadLetteringOnMessageExpiration: false, maxDeliveryCount: 7, enableBatchedOperations: true,
                status: EntityStatus.ReceiveDisabled, forwardTo: string.Empty,
                forwardDeadLetteredMessagesTo: string.Empty, userMetadata: "deferred");
            return Task.FromResult(Response.FromValue(properties, null!));
        }

        public override AsyncPageable<RuleProperties> GetRulesAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Calls.Add("rules");
            if (FailRules) throw new InvalidOperationException("Cannot read rules.");
            var rule = ServiceBusModelFactory.RuleProperties("DeferredFilter",
                new SqlRuleFilter("user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL"));
            var rules = new List<RuleProperties>();
            if (RuleCount > 0) rules.Add(rule);
            if (RuleCount > 1) rules.Add(ServiceBusModelFactory.RuleProperties("custom", new SqlRuleFilter("user.Custom = 1"),
                new SqlRuleAction("SET user.Source = 'retained'")));
            return AsyncPageable<RuleProperties>.FromPages(new[] { Page<RuleProperties>.FromValues(rules, null, null!) });
        }

        public override Task<Response> DeleteSubscriptionAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete");
            return Task.FromResult<Response>(null!);
        }

        public override Task<Response<SubscriptionProperties>> CreateSubscriptionAsync(
            CreateSubscriptionOptions options, CreateRuleOptions rule, CancellationToken cancellationToken = default)
        {
            Calls.Add("create");
            if (FailCreate) throw new InvalidOperationException("Cannot recreate subscription.");
            Created = options;
            CreatedRule = rule;
            return Task.FromResult<Response<SubscriptionProperties>>(null!);
        }

        public override Task<Response<RuleProperties>> CreateRuleAsync(
            string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("create-rule:" + options.Name);
            AddedRule = options;
            return Task.FromResult<Response<RuleProperties>>(null!);
        }

        public override Task<Response> DeleteRuleAsync(
            string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete-rule:" + ruleName);
            return Task.FromResult<Response>(null!);
        }
    }

    private sealed class RecordingServiceBusManagement : IServiceBusManagement
    {
        public List<string> Calls { get; } = new();

        public int? FailingCall { get; init; }

        public Task CreateCustomRule(
            string topicName,
            string subscriptionName,
            string ruleName,
            string filter,
            string action)
            => Record($"CreateCustomRule|{topicName}|{subscriptionName}|{ruleName}|{filter}|{action ?? "<null>"}");

        public Task CreateSubscription(string topicName, string subscriptionName)
            => Record($"CreateSubscription|{topicName}|{subscriptionName}");

        public Task RecreateSubscription(string topicName, string subscriptionName)
            => Record($"RecreateSubscription|{topicName}|{subscriptionName}");

        public Task DeleteRule(string topicName, string subscriptionName, string ruleName)
            => Record($"DeleteRule|{topicName}|{subscriptionName}|{ruleName}");

        public Task DeleteSubscription(string topicName, string subscriptionName)
            => Record($"DeleteSubscription|{topicName}|{subscriptionName}");

        // Topic deletion is WebApp admin surface; ClearEndpoint never removes the topic itself.
        public Task DeleteTopic(string topicName)
            => throw new NotSupportedException();

        public Task DisableSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task EnableSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task<bool> IsSubscriptionActive(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task DisableTopicSend(string topicName)
            => throw new NotSupportedException();

        public Task EnableTopicSend(string topicName)
            => throw new NotSupportedException();

        public Task<TopicSendState> GetTopicSendState(string topicName)
            => throw new NotSupportedException();

        public Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo)
            => throw new NotSupportedException();

        // Read/inspect surface used by the WebApp's subscription admin, not by ClearEndpoint.
        public Task UpdateSubscription(
            string topicName, string subscriptionName, EntityStatus status, string forwardTo, bool changeForwardTo)
            => throw new NotSupportedException();

        public Task<SubscriptionProperties> GetSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TopicProperties> ListTopicsAsync()
            => throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionProperties> ListSubscriptionsAsync(string topicName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RuleProperties> ListRulesAsync(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TopicRuntimeProperties> ListTopicRuntimePropertiesAsync()
            => throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string topicName)
            => throw new NotSupportedException();

        private Task Record(string call)
        {
            Calls.Add(call);
            if (FailingCall == Calls.Count - 1)
                throw new InvalidOperationException("Simulated management failure.");

            return Task.CompletedTask;
        }
    }
}
