#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using System;

namespace NimBus.Core.Tests;

[TestClass]
public class RetryPolicyProviderTests
{
    private static RetryPolicy MakePolicy(int maxRetries = 3) => new()
    {
        MaxRetries = maxRetries,
        Strategy = BackoffStrategy.Fixed,
        BaseDelay = TimeSpan.FromMinutes(1)
    };

    // ── Default policy ──────────────────────────────────────────────

    [TestMethod]
    public void GetRetryPolicy_NoConfig_ReturnsNull()
    {
        var provider = new DefaultRetryPolicyProvider();
        var result = provider.GetRetryPolicy("OrderPlaced", "some error");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetRetryPolicy_DefaultPolicySet_ReturnsFallback()
    {
        var policy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.SetDefaultPolicy(policy);

        var result = provider.GetRetryPolicy("AnyEvent", "any exception");
        Assert.AreSame(policy, result);
    }

    // ── Event-type policies ─────────────────────────────────────────

    [TestMethod]
    public void GetRetryPolicy_EventTypeMatch_ReturnsEventPolicy()
    {
        var eventPolicy = MakePolicy(10);
        var defaultPolicy = MakePolicy(1);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddEventTypePolicy("OrderPlaced", eventPolicy);
        provider.SetDefaultPolicy(defaultPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", "some error");
        Assert.AreSame(eventPolicy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_EventTypeMismatch_ReturnsDefault()
    {
        var eventPolicy = MakePolicy(10);
        var defaultPolicy = MakePolicy(1);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddEventTypePolicy("OrderPlaced", eventPolicy);
        provider.SetDefaultPolicy(defaultPolicy);

        var result = provider.GetRetryPolicy("PaymentCaptured", "some error");
        Assert.AreSame(defaultPolicy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_EventTypeCaseInsensitive()
    {
        var policy = MakePolicy();
        var provider = new DefaultRetryPolicyProvider();
        provider.AddEventTypePolicy("OrderPlaced", policy);

        var result = provider.GetRetryPolicy("orderplaced", "error");
        Assert.AreSame(policy, result);
    }

    // ── Exception rules ─────────────────────────────────────────────

    [TestMethod]
    public void GetRetryPolicy_ExceptionRuleMatches_ReturnsRulePolicy()
    {
        var timeoutPolicy = MakePolicy(2);
        var defaultPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", timeoutPolicy);
        provider.SetDefaultPolicy(defaultPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", "Connection timeout after 30s");
        Assert.AreSame(timeoutPolicy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_ExceptionRuleCaseInsensitive()
    {
        var policy = MakePolicy();
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", policy);

        var result = provider.GetRetryPolicy("AnyEvent", "TIMEOUT occurred");
        Assert.AreSame(policy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_ExceptionRuleDoesNotMatch_FallsToEventType()
    {
        var exceptionPolicy = MakePolicy(1);
        var eventPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", exceptionPolicy);
        provider.AddEventTypePolicy("OrderPlaced", eventPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", "NullReferenceException");
        Assert.AreSame(eventPolicy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_ExceptionRuleTakesPrecedenceOverEventType()
    {
        var exceptionPolicy = MakePolicy(1);
        var eventPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", exceptionPolicy);
        provider.AddEventTypePolicy("OrderPlaced", eventPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", "Connection timeout");
        Assert.AreSame(exceptionPolicy, result, "Exception rule should take precedence over event-type policy");
    }

    [TestMethod]
    public void GetRetryPolicy_ExceptionRuleScopedToEventType_MatchesOnlyScoped()
    {
        var scopedPolicy = MakePolicy(2);
        var defaultPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", scopedPolicy, "OrderPlaced");
        provider.SetDefaultPolicy(defaultPolicy);

        var matchResult = provider.GetRetryPolicy("OrderPlaced", "timeout error");
        Assert.AreSame(scopedPolicy, matchResult);

        var noMatchResult = provider.GetRetryPolicy("PaymentCaptured", "timeout error");
        Assert.AreSame(defaultPolicy, noMatchResult, "Scoped rule should not match other event types");
    }

    [TestMethod]
    public void GetRetryPolicy_NullExceptionMessage_SkipsExceptionRules()
    {
        var exceptionPolicy = MakePolicy(1);
        var defaultPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", exceptionPolicy);
        provider.SetDefaultPolicy(defaultPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", null);
        Assert.AreSame(defaultPolicy, result);
    }

    [TestMethod]
    public void GetRetryPolicy_EmptyExceptionMessage_SkipsExceptionRules()
    {
        var exceptionPolicy = MakePolicy(1);
        var defaultPolicy = MakePolicy(5);
        var provider = new DefaultRetryPolicyProvider();
        provider.AddExceptionRule("timeout", exceptionPolicy);
        provider.SetDefaultPolicy(defaultPolicy);

        var result = provider.GetRetryPolicy("OrderPlaced", "");
        Assert.AreSame(defaultPolicy, result);
    }

    // ── Validation ──────────────────────────────────────────────────

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void AddEventTypePolicy_NullPolicy_Throws()
    {
        new DefaultRetryPolicyProvider().AddEventTypePolicy("OrderPlaced", null);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void AddExceptionRule_NullContains_Throws()
    {
        new DefaultRetryPolicyProvider().AddExceptionRule(null, MakePolicy());
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void AddExceptionRule_NullPolicy_Throws()
    {
        new DefaultRetryPolicyProvider().AddExceptionRule("timeout", null);
    }
}
