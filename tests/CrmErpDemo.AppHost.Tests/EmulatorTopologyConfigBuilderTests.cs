#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CrmErpDemo.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class EmulatorTopologyConfigBuilderTests
{
    // Parse the generated config JSON once for all assertions in each test.
    private static JsonDocument BuildConfig() =>
        JsonDocument.Parse(EmulatorTopologyConfigBuilder.Build(new CrmErpPlatformConfiguration()));

    [TestMethod]
    public void Build_DynamicForward_AgentZoneEndpoint_HasForwardSubscriptionToDataPlatform()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "AgentZoneEndpoint");

        Assert.IsNotNull(topic, "AgentZoneEndpoint topic not found in generated config");

        var sub = FindSubscription(topic.Value, "AgentDyn-DataPlatformEndpoint");
        Assert.IsNotNull(sub, "Subscription 'AgentDyn-DataPlatformEndpoint' not found on AgentZoneEndpoint topic");
    }

    [TestMethod]
    public void Build_DynamicForward_AgentZoneEndpoint_SubscriptionForwardsToDatPlatformEndpoint()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "AgentZoneEndpoint");
        var sub = FindSubscription(topic!.Value, "AgentDyn-DataPlatformEndpoint");

        var forwardTo = sub!.Value
            .GetProperty("Properties")
            .GetProperty("ForwardTo")
            .GetString();

        Assert.AreEqual("DataPlatformEndpoint", forwardTo,
            "ForwardTo on AgentDyn-DataPlatformEndpoint subscription must point to DataPlatformEndpoint");
    }

    [TestMethod]
    public void Build_DynamicForward_AgentZoneEndpoint_RuleFilterMatchesDynamicEventType()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "AgentZoneEndpoint");
        var sub = FindSubscription(topic!.Value, "AgentDyn-DataPlatformEndpoint");

        var rule = FindRule(sub!.Value, "dyn-crm.contact.enriched.v1");
        Assert.IsNotNull(rule, "Rule 'dyn-crm.contact.enriched.v1' not found in AgentDyn-DataPlatformEndpoint subscription");

        var sqlFilter = rule.Value
            .GetProperty("Properties")
            .GetProperty("SqlFilter")
            .GetProperty("SqlExpression")
            .GetString();

        StringAssert.Contains(sqlFilter, "user.EventTypeId = 'crm.contact.enriched.v1'",
            "Filter must match on crm.contact.enriched.v1 EventTypeId");
        StringAssert.Contains(sqlFilter, "user.From IS NULL",
            "Filter must include 'user.From IS NULL' loop-prevention guard");
    }

    [TestMethod]
    public void Build_DynamicForward_AgentZoneEndpoint_RuleActionSetsFromAndToCorrectly()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "AgentZoneEndpoint");
        var sub = FindSubscription(topic!.Value, "AgentDyn-DataPlatformEndpoint");
        var rule = FindRule(sub!.Value, "dyn-crm.contact.enriched.v1");

        var action = rule!.Value
            .GetProperty("Properties")
            .GetProperty("Action")
            .GetProperty("SqlExpression")
            .GetString();

        StringAssert.Contains(action, "SET user.From = 'AgentZoneEndpoint'",
            "Action must set From to AgentZoneEndpoint");
        StringAssert.Contains(action, "SET user.EventId = newid()",
            "Action must assign a new EventId");
        StringAssert.Contains(action, "SET user.To = 'DataPlatformEndpoint'",
            "Action must set To to DataPlatformEndpoint");
    }

    [TestMethod]
    public void Build_CompiledForwards_AreNotAffected()
    {
        // Sanity check: the existing compiled-forward rules still exist after
        // the dynamic-forward addition. CrmEndpoint produces events consumed by ErpEndpoint.
        using var doc = BuildConfig();
        var crmTopic = FindTopic(doc, "CrmEndpoint");
        Assert.IsNotNull(crmTopic, "CrmEndpoint topic must still be present");

        // CRM produces CrmAccountCreated etc — ErpEndpoint consumes some of them.
        // At least one forward subscription from CRM to ERP should exist.
        var erpForwardSub = FindSubscription(crmTopic.Value, "ErpEndpoint");
        Assert.IsNotNull(erpForwardSub, "Compiled forward subscription CrmEndpoint → ErpEndpoint must still exist");
    }

    // ── CloudEvents partner interop entities ──────────────────────────────────

    [TestMethod]
    public void Build_PartnerInboundTopic_HasSessionRequiredCatchAllCrmEndpointSubscription()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "PartnerInbound");
        Assert.IsNotNull(topic, "PartnerInbound topic not found in generated config");

        var sub = FindSubscription(topic.Value, "CrmEndpoint");
        Assert.IsNotNull(sub, "Subscription 'CrmEndpoint' not found on PartnerInbound topic");

        // NimBus receivers are session processors, so the partner ingress
        // subscription must require sessions.
        Assert.IsTrue(
            sub.Value.GetProperty("Properties").GetProperty("RequiresSession").GetBoolean(),
            "PartnerInbound/CrmEndpoint must require sessions — NimBus receivers are ServiceBusSessionProcessors");

        // Raw CloudEvents producers stamp no user.* routing properties, so the
        // subscription must be catch-all (default 1=1 rule), not user.To-filtered.
        var rule = FindRule(sub.Value, "$Default");
        Assert.IsNotNull(rule, "PartnerInbound/CrmEndpoint must keep the catch-all $Default rule");
        Assert.AreEqual(
            "1=1",
            rule.Value.GetProperty("Properties").GetProperty("SqlFilter").GetProperty("SqlExpression").GetString(),
            "Catch-all rule must be 1=1 — external CloudEvents carry no user.To");
    }

    [TestMethod]
    public void Build_ErpEndpointTopic_HasPartnerPortalCaptureSubscriptionFilteredToOriginalPublishes()
    {
        using var doc = BuildConfig();
        var topic = FindTopic(doc, "ErpEndpoint");
        Assert.IsNotNull(topic, "ErpEndpoint topic not found in generated config");

        var sub = FindSubscription(topic.Value, "PartnerPortalCapture");
        Assert.IsNotNull(sub, "Subscription 'PartnerPortalCapture' not found on ErpEndpoint topic");

        Assert.IsFalse(
            sub.Value.GetProperty("Properties").GetProperty("RequiresSession").GetBoolean(),
            "PartnerPortalCapture is drained by a plain (non-NimBus) receiver and must not require sessions");

        var rule = FindRule(sub.Value, "cloudevents-capture");
        Assert.IsNotNull(rule, "Rule 'cloudevents-capture' not found on PartnerPortalCapture subscription");

        var sqlFilter = rule.Value
            .GetProperty("Properties")
            .GetProperty("SqlFilter")
            .GetProperty("SqlExpression")
            .GetString();

        StringAssert.Contains(sqlFilter, "user.MessageType = 'EventRequest'",
            "Capture must keep only original publishes (EventRequest)");
        StringAssert.Contains(sqlFilter, "user.From IS NULL",
            "Capture must exclude forwarded/rewritten copies");
    }

    [TestMethod]
    public void Build_PartnerPortalCapture_OnlyOnErpEndpointTopic()
    {
        // The capture subscription is ERP-specific: no other endpoint topic
        // (and not PartnerInbound) should grow one.
        using var doc = BuildConfig();
        var topics = doc.RootElement
            .GetProperty("UserConfig")
            .GetProperty("Namespaces")[0]
            .GetProperty("Topics");

        foreach (var topic in topics.EnumerateArray())
        {
            var name = topic.GetProperty("Name").GetString();
            if (name == "ErpEndpoint") continue;

            Assert.IsNull(
                FindSubscription(topic, "PartnerPortalCapture"),
                $"Topic '{name}' must not have a PartnerPortalCapture subscription");
        }
    }

    // ── Two forwards, same (source, target), different event types ────────────

    [TestMethod]
    public void Build_TwoDynamicForwards_SameSourceAndTarget_ProduceOneSubscriptionWithBothRules()
    {
        // Regression: the dynamic-forward pass keyed dedup on the AgentDyn-{target}
        // subscription name and `continue`d once that name was added, silently
        // dropping a SECOND forward for the same (source, target) pair with a
        // different EventTypeId. ServiceBusTopologyProvisioner instead adds one rule
        // per forward to the same forward subscription — this must match.
        var platform = new TwoForwardPlatform();

        using var doc = JsonDocument.Parse(EmulatorTopologyConfigBuilder.Build(platform));
        var topic = FindTopic(doc, "SourceEndpoint");
        Assert.IsNotNull(topic, "SourceEndpoint topic not found");

        // Exactly one AgentDyn-TargetEndpoint subscription — not one per forward.
        var agentDynCount = topic!.Value.GetProperty("Subscriptions").EnumerateArray()
            .Count(s => s.GetProperty("Name").GetString() == "AgentDyn-TargetEndpoint");
        Assert.AreEqual(1, agentDynCount, "Both forwards must collapse onto a single AgentDyn-TargetEndpoint subscription");

        var sub = FindSubscription(topic.Value, "AgentDyn-TargetEndpoint");
        Assert.IsNotNull(FindRule(sub!.Value, "dyn-evt.one.v1"), "First forward's rule 'dyn-evt.one.v1' must be present");
        Assert.IsNotNull(FindRule(sub.Value, "dyn-evt.two.v1"), "Second forward's rule 'dyn-evt.two.v1' must not be dropped");
    }

    // ---------------------------------------------------------------------------
    // Test platform: two dynamic forwards sharing source + target
    // ---------------------------------------------------------------------------

    private sealed class TwoForwardPlatform : Platform
    {
        private static readonly IReadOnlyList<DynamicForward> Forwards = new[]
        {
            new DynamicForward("SourceEndpoint", "evt.one.v1", "TargetEndpoint"),
            new DynamicForward("SourceEndpoint", "evt.two.v1", "TargetEndpoint"),
        };

        public TwoForwardPlatform()
        {
            AddEndpoint(new TestEndpoint("SourceEndpoint"));
            AddEndpoint(new TestEndpoint("TargetEndpoint"));
        }

        public override IReadOnlyList<DynamicForward> DynamicForwards => Forwards;
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

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static JsonElement? FindTopic(JsonDocument doc, string topicName)
    {
        var topics = doc.RootElement
            .GetProperty("UserConfig")
            .GetProperty("Namespaces")[0]
            .GetProperty("Topics");

        foreach (var topic in topics.EnumerateArray())
        {
            if (topic.GetProperty("Name").GetString() == topicName)
                return topic;
        }

        return null;
    }

    private static JsonElement? FindSubscription(JsonElement topic, string subscriptionName)
    {
        foreach (var sub in topic.GetProperty("Subscriptions").EnumerateArray())
        {
            if (sub.GetProperty("Name").GetString() == subscriptionName)
                return sub;
        }

        return null;
    }

    private static JsonElement? FindRule(JsonElement subscription, string ruleName)
    {
        foreach (var rule in subscription.GetProperty("Rules").EnumerateArray())
        {
            if (rule.GetProperty("Name").GetString() == ruleName)
                return rule;
        }

        return null;
    }
}
