#pragma warning disable CA1707, CA2007

using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Mapper round-trips audit types by name (<c>Enum.Parse&lt;MessageAuditAuditType&gt;</c>),
/// so a <see cref="MessageAuditType"/> value missing from api-spec.yaml doesn't fail at the
/// write — it throws later, on every audit search, endpoint audit and CSV export that
/// touches such a row. GrantRole and RevokeRole shipped that way and were only found by
/// reading the spec next to the enum.
/// </summary>
[TestClass]
public sealed class AuditTypeContractTests
{
    [TestMethod]
    public void EveryPersistedAuditType_ParsesIntoTheGeneratedContractEnums()
    {
        var missing = Enum.GetNames<MessageAuditType>()
            .Where(name =>
                !Enum.TryParse<MessageAuditAuditType>(name, out _) ||
                !Enum.TryParse<AuditSearchFilterAuditType>(name, out _))
            .ToList();

        Assert.AreEqual(
            0,
            missing.Count,
            $"Add to every auditType enum in api-spec.yaml (camelCase): {string.Join(", ", missing)}");
    }
}
