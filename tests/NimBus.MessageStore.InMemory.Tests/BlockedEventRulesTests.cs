#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public class BlockedEventRulesTests
{
    [TestMethod]
    public void ResolveOriginatingId_Self_ReturnsLastMessageId()
    {
        Assert.AreEqual("msg-42", BlockedEventRules.ResolveOriginatingId("self", "msg-42"));
    }

    [TestMethod]
    public void ResolveOriginatingId_SelfIsCaseInsensitive()
    {
        Assert.AreEqual("msg-42", BlockedEventRules.ResolveOriginatingId("Self", "msg-42"));
        Assert.AreEqual("msg-42", BlockedEventRules.ResolveOriginatingId("SELF", "msg-42"));
    }

    [TestMethod]
    public void ResolveOriginatingId_SelfWithNullLastMessageId_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, BlockedEventRules.ResolveOriginatingId("self", null));
    }

    [TestMethod]
    public void ResolveOriginatingId_NonSelf_PassesThrough()
    {
        Assert.AreEqual("origin-1", BlockedEventRules.ResolveOriginatingId("origin-1", "msg-42"));
    }

    [TestMethod]
    public void ResolveOriginatingId_NullOriginating_ReturnsEmpty()
    {
        // The Cosmos provider historically NRE'd here; the shared rule must be null-safe.
        Assert.AreEqual(string.Empty, BlockedEventRules.ResolveOriginatingId(null, "msg-42"));
    }

    [TestMethod]
    [DataRow("self")]
    [DataRow("Self")]
    [DataRow("SELF")]
    public void IsSelfOriginating_SelfAnyCase_True(string value)
    {
        Assert.IsTrue(BlockedEventRules.IsSelfOriginating(value));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("origin-1")]
    public void IsSelfOriginating_NullEmptyOrOther_False(string? value)
    {
        Assert.IsFalse(BlockedEventRules.IsSelfOriginating(value));
    }
}
