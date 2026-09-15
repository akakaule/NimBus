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
/// the instance count) produced a 429 storm in production. The values below were chosen
/// deliberately and must not drift back silently through an unrelated host.json edit.
/// </summary>
[TestClass]
public class ResolverHostConfigurationTests
{
    private static JsonElement LoadServiceBusSection(out JsonElement root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "host.json");
        Assert.IsTrue(File.Exists(path), $"host.json was not copied to the test output: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        root = document.RootElement.Clone();
        return root.GetProperty("extensions").GetProperty("serviceBus");
    }

    [TestMethod]
    public void HostJson_DisablesAutoComplete()
    {
        var serviceBus = LoadServiceBusSection(out _);

        Assert.IsFalse(serviceBus.GetProperty("autoCompleteMessages").GetBoolean());
    }

    [TestMethod]
    public void HostJson_BoundsSessionConcurrencyTo16()
    {
        var serviceBus = LoadServiceBusSection(out _);

        Assert.AreEqual(16, serviceBus.GetProperty("maxConcurrentSessions").GetInt32());
    }

    [TestMethod]
    public void HostJson_DisablesPrefetch()
    {
        var serviceBus = LoadServiceBusSection(out _);

        Assert.AreEqual(0, serviceBus.GetProperty("prefetchCount").GetInt32());
    }

    [TestMethod]
    public void HostJson_UsesOneSecondSessionIdleTimeout()
    {
        var serviceBus = LoadServiceBusSection(out _);

        Assert.AreEqual("00:00:01", serviceBus.GetProperty("sessionIdleTimeout").GetString());
    }

    [TestMethod]
    public void HostJson_KeepsFiveMinuteLockRenewal()
    {
        var serviceBus = LoadServiceBusSection(out _);

        Assert.AreEqual("00:05:00", serviceBus.GetProperty("maxAutoLockRenewalDuration").GetString());
    }

    [TestMethod]
    public void HostJson_DisablesDynamicConcurrency()
    {
        _ = LoadServiceBusSection(out var root);

        Assert.IsFalse(root.GetProperty("concurrency").GetProperty("dynamicConcurrencyEnabled").GetBoolean());
    }
}
