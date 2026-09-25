#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public class EndpointVisibilityTests
{
    [TestMethod]
    [DataRow("dev")]
    [DataRow("SBDEV")]
    public void Demo_endpoints_are_listed_in_dev_environments(string environment)
        => Assert.IsTrue(EndpointVisibility.IsListed("Alice", environment));

    [TestMethod]
    [DataRow("prod")]
    [DataRow(null)]
    public void Demo_endpoints_are_hidden_elsewhere(string? environment)
        => Assert.IsFalse(EndpointVisibility.IsListed("bob", environment));

    [TestMethod]
    public void Other_endpoints_are_listed_without_a_configured_environment()
        => Assert.IsTrue(EndpointVisibility.IsListed("CrmEndpoint", null));
}
