#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public class EndpointErrorListFormatTests
{
    [TestMethod]
    public void Format_Empty_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, EndpointErrorListFormat.Format([]));
    }

    [TestMethod]
    public void Format_AppendsTrailingSeparator()
    {
        Assert.AreEqual("a;", EndpointErrorListFormat.Format(["a"]));
        Assert.AreEqual("a;b;c;", EndpointErrorListFormat.Format(["a", "b", "c"]));
    }

    [TestMethod]
    public void StatusConstants_MatchProviderLiterals()
    {
        Assert.AreEqual("Failed", EndpointErrorListFormat.FailedStatus);
        Assert.AreEqual("Deferred", EndpointErrorListFormat.DeferredStatus);
    }
}
