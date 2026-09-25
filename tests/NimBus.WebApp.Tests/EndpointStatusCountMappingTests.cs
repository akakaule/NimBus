#pragma warning disable CA1707, CA2007

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.WebApp.Controllers;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The Monitor wall reads "failing for" from <c>oldestFailureAt</c>; the mapper must
/// carry the store's value through alongside the folded failed count.
/// </summary>
[TestClass]
public class EndpointStatusCountMappingTests
{
    [TestMethod]
    public void Maps_oldest_failure_and_folds_dead_letters_into_failed()
    {
        var oldest = new DateTime(2026, 9, 25, 6, 30, 0, DateTimeKind.Utc);

        var status = Mapper.EndpointStatusCountFromEndpointStateCount(new EndpointStateCount
        {
            EndpointId = "billing",
            FailedCount = 2,
            DeadletterCount = 3,
            OldestFailureAt = oldest,
        });

        Assert.AreEqual(oldest, status.OldestFailureAt);
        Assert.AreEqual(5, status.FailedCount);
    }

    [TestMethod]
    public void Leaves_oldest_failure_null_without_open_failures()
    {
        var status = Mapper.EndpointStatusCountFromEndpointStateCount(new EndpointStateCount { EndpointId = "billing" });

        Assert.IsNull(status.OldestFailureAt);
    }
}
