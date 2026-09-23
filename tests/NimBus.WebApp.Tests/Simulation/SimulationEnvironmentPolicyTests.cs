#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// The environment gate (plan Decision 4): fail closed on a missing environment, never allow a
/// production name, and otherwise require the allowlist.
/// </summary>
[TestClass]
public sealed class SimulationEnvironmentPolicyTests
{
    private static readonly string[] Defaults = new SimulationOptions().AllowedEnvironments.ToArray();

    [TestMethod]
    [DataRow("dev")]
    [DataRow("Development")]
    [DataRow(" DEV ")]
    public void Dev_environments_are_allowed_by_default(string environment)
    {
        var (allowed, reason) = SimulationEnvironmentPolicy.Evaluate(environment, Defaults);

        Assert.IsTrue(allowed);
        Assert.IsNull(reason);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Missing_or_blank_environment_is_blocked(string? environment)
    {
        var (allowed, reason) = SimulationEnvironmentPolicy.Evaluate(environment, Defaults);

        Assert.IsFalse(allowed);
        Assert.AreEqual(SimulationBlockedReason.EnvironmentMissing, reason);
    }

    [TestMethod]
    [DataRow("prod")]
    [DataRow("Production")]
    [DataRow("PRD")]
    [DataRow("live")]
    public void Production_names_are_blocked_even_when_listed(string environment)
    {
        var (allowed, reason) = SimulationEnvironmentPolicy.Evaluate(environment, new[] { "dev", environment });

        Assert.IsFalse(allowed);
        Assert.AreEqual(SimulationBlockedReason.Production, reason);
    }

    [TestMethod]
    public void Test_is_blocked_unless_listed()
    {
        Assert.AreEqual(SimulationBlockedReason.NotAllowed, SimulationEnvironmentPolicy.Evaluate("test", Defaults).Reason);
        Assert.IsTrue(SimulationEnvironmentPolicy.Evaluate("test", new List<string>(Defaults) { "test" }).Allowed);
    }
}
