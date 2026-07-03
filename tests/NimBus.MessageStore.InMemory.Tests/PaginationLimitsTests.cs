#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public class PaginationLimitsTests
{
    [TestMethod]
    [DataRow(0, PaginationLimits.DefaultPageSize)]   // unset -> default
    [DataRow(-1, PaginationLimits.DefaultPageSize)]  // negative -> default
    [DataRow(-1000, PaginationLimits.DefaultPageSize)]
    [DataRow(1, 1)]                                   // smallest valid passes through
    [DataRow(50, 50)]                                 // typical page passes through
    [DataRow(1000, 1000)]                             // at the cap passes through
    [DataRow(1001, PaginationLimits.MaxPageSize)]     // above the cap clamps down
    [DataRow(int.MaxValue, PaginationLimits.MaxPageSize)] // OOM-sized request clamps down
    public void Resolve_ClampsToExpectedPageSize(int requested, int expected)
    {
        Assert.AreEqual(expected, PaginationLimits.Resolve(requested));
    }
}
