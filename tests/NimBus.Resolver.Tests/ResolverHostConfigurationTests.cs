#pragma warning disable CA1707, CA1515, CA2007
using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Resolver.Tests;

/// <summary>
/// Pins the Resolver's Service Bus consumer settings in host.json. The Resolver is a
/// downstream-limited consumer: every message ends in Cosmos writes, so its real ceiling is
/// the Cosmos RU budget, not the bus. The previous 200 sessions per instance (multiplied by
/// the instance count) produced a 429 storm in production. host.json owns the fixed bounds
/// below; only <c>maxConcurrentSessions</c> is also a template-owned app setting
/// (<c>deploy/bicep/deploy.core.bicep</c>), which wins over this default when set. The values
/// must not drift back silently through an unrelated host.json edit.
/// </summary>
[TestClass]
public class ResolverHostConfigurationTests
{
    // Linked into the test output under its own name by the test csproj, so the pin does not
    // depend on the Functions SDK's transitive host.json copy.
    private const string FixtureName = "Resolver.host.json";

    [TestMethod]
    public void HostJson_PinsTheDownstreamLimitedConsumerShape()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FixtureName);
        Assert.IsTrue(File.Exists(path), $"{FixtureName} was not copied to the test output: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var serviceBus = root.GetProperty("extensions").GetProperty("serviceBus");

        Assert.IsFalse(serviceBus.GetProperty("autoCompleteMessages").GetBoolean(), "The Resolver settles messages itself.");
        Assert.AreEqual(16, serviceBus.GetProperty("maxConcurrentSessions").GetInt32(), "Sessions per instance; the tunable bound.");
        Assert.AreEqual(0, serviceBus.GetProperty("prefetchCount").GetInt32(), "Prefetched locks would wait for a slot.");
        Assert.AreEqual("00:00:01", serviceBus.GetProperty("sessionIdleTimeout").GetString(), "A drained per-aggregate session must release its slot at once.");
        Assert.AreEqual("00:05:00", serviceBus.GetProperty("maxAutoLockRenewalDuration").GetString());
        Assert.IsFalse(root.GetProperty("concurrency").GetProperty("dynamicConcurrencyEnabled").GetBoolean(), "Dynamic concurrency would override the manual session bound.");
    }
}
