#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The retention option is the only operator-visible knob on unresolved-row TTL, so
/// its validation boundaries and its day-to-second conversion are pinned here rather
/// than inferred from the writes that consume them.
/// </summary>
[TestClass]
public sealed class CosmosDbMessageStoreOptionsTests
{
    [TestMethod]
    public void Default_is_unlimited()
    {
        var options = new CosmosDbMessageStoreOptions();

        Assert.AreEqual(CosmosDbMessageStoreOptions.UnlimitedRetentionDays, options.UnresolvedRetentionDays);
        Assert.AreEqual(-1, CosmosDbMessageStoreOptions.UnlimitedRetentionDays);
        Assert.AreEqual(365, CosmosDbMessageStoreOptions.MaxRetentionDays);
        Assert.AreEqual("NimBus:Cosmos", CosmosDbMessageStoreOptions.SectionName);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    [DataRow(180)]
    [DataRow(365)]
    public void Validate_accepts_supported_values(int days)
    {
        var options = new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = days };

        options.Validate();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-2)]
    [DataRow(366)]
    [DataRow(int.MinValue)]
    [DataRow(int.MaxValue)]
    public void Validate_rejects_unsupported_values(int days)
    {
        var options = new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = days };

        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => options.Validate());

        StringAssert.Contains(ex.Message, nameof(CosmosDbMessageStoreOptions.UnresolvedRetentionDays),
            "The failure must name the option so an operator can find it.");
        StringAssert.Contains(ex.Message, days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "The failure must quote the offending value.");
    }

    [TestMethod]
    [DataRow(-1, -1)]
    [DataRow(1, 86_400)]
    [DataRow(180, 15_552_000)]
    [DataRow(365, 31_536_000)]
    public void ResolveUnresolvedTimeToLiveSeconds_converts_whole_days(int days, int expectedSeconds)
    {
        var options = new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = days };

        Assert.AreEqual(expectedSeconds, options.ResolveUnresolvedTimeToLiveSeconds());
    }
}
