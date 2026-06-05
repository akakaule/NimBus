#pragma warning disable CA1707, CA2007
using System.Text.Json;
using CrmErpDemo.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

        var rule = FindRule(sub!.Value, "crm.contact.enriched.v1");
        Assert.IsNotNull(rule, "Rule 'crm.contact.enriched.v1' not found in AgentDyn-DataPlatformEndpoint subscription");

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
        var rule = FindRule(sub!.Value, "crm.contact.enriched.v1");

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
