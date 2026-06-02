#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;
using System;
using System.Globalization;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusFilterValidatorTests
{
    // The values that flow through ServiceBusFilterValidator in production:
    // NimBus endpoint ids, NimBus.Core.Messages.Constants ids, composed
    // subscription/rule names (e.g. "to-Crm", "from-Crm"), and Service Bus's
    // own $Default rule name. All must be accepted.
    [DataTestMethod]
    [DataRow("Alice")]
    [DataRow("CrmEndpoint")]
    [DataRow("ErpEndpoint")]
    [DataRow("Resolver")]
    [DataRow("Manager")]
    [DataRow("Continuation")]
    [DataRow("Retry")]
    [DataRow("Deferred")]
    [DataRow("DeferredProcessor")]
    [DataRow("$Default")]
    [DataRow("to-CrmEndpoint")]   // composed rule name
    [DataRow("from-CrmEndpoint")] // composed rule name
    [DataRow("a")]                // single char
    [DataRow("a.b_c-d$1")]        // every allowed char class
    public void ValidateName_AcceptsValidValue(string value)
    {
        // Must not throw.
        ServiceBusFilterValidator.ValidateName(value, "param");
    }

    // Values containing characters that would terminate the surrounding
    // single-quoted SQL filter or inject filter syntax. Each must be
    // rejected before reaching SqlRuleFilter.
    [DataTestMethod]
    [DataRow("a' OR 1=1 OR 'b")] // quote injection
    [DataRow("foo'")]
    [DataRow("foo\"bar")]
    [DataRow("foo bar")] // space
    [DataRow("foo;bar")] // statement terminator
    [DataRow("foo(bar)")]
    [DataRow("foo,bar")]
    [DataRow("foo/bar")]
    [DataRow("foo\\bar")]
    [DataRow("foo\tbar")]
    [DataRow("foo\nbar")]
    public void ValidateName_RejectsValueWithFilterMetacharacters(string value)
    {
        var ex = Assert.ThrowsException<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void ValidateName_RejectsNullOrEmpty(string value)
    {
        var ex = Assert.ThrowsException<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
    }

    [TestMethod]
    public void ValidateName_RejectsOverlongValue()
    {
        var value = new string('a', ServiceBusFilterValidator.MaxNameLength + 1);
        var ex = Assert.ThrowsException<ArgumentException>(
            () => ServiceBusFilterValidator.ValidateName(value, "param"));
        Assert.AreEqual("param", ex.ParamName);
        StringAssert.Contains(ex.Message, ServiceBusFilterValidator.MaxNameLength.ToString(CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void ValidateName_AcceptsValueAtMaxLength()
    {
        var value = new string('a', ServiceBusFilterValidator.MaxNameLength);
        ServiceBusFilterValidator.ValidateName(value, "param");
    }
}
