#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Regression guard for the enum contract between the domain
/// <see cref="MessageAuditType"/> and the NSwag-generated audit DTO enums.
///
/// The read paths in Mapper.MessageAuditFromMessageAuditEntity and
/// AuditImplementation.PostAuditsSearchAsync do a hard
/// <c>Enum.Parse&lt;...&gt;(messageAuditType.ToString())</c>. If a value is added
/// to <see cref="MessageAuditType"/> but the api-spec.yaml enums are not
/// regenerated, every audit row carrying the new value makes those reads throw
/// (HTTP 500). These tests fail the build the moment the enums drift, forcing a
/// regen instead of a production 500.
/// </summary>
[TestClass]
public sealed class MessageAuditTypeContractTests
{
    [TestMethod]
    public void EveryMessageAuditType_ParsesInto_MessageAuditAuditType()
    {
        foreach (MessageAuditType type in Enum.GetValues<MessageAuditType>())
        {
            // Mirrors Mapper.MessageAuditFromMessageAuditEntity (per-event audit load).
            Assert.IsTrue(
                Enum.TryParse<MessageAuditAuditType>(type.ToString(), out _),
                $"MessageAuditType.{type} has no matching MessageAuditAuditType member — " +
                "add it to api-spec.yaml and regenerate the NSwag client.");
        }
    }

    [TestMethod]
    public void EveryMessageAuditType_ParsesInto_AuditEntryAuditType()
    {
        foreach (MessageAuditType type in Enum.GetValues<MessageAuditType>())
        {
            // Mirrors AuditImplementation.PostAuditsSearchAsync (audit-search read).
            Assert.IsTrue(
                Enum.TryParse<AuditEntryAuditType>(type.ToString(), out _),
                $"MessageAuditType.{type} has no matching AuditEntryAuditType member — " +
                "add it to api-spec.yaml and regenerate the NSwag client.");
        }
    }

    [TestMethod]
    public void EveryMessageAuditType_ParsesInto_AuditSearchFilterAuditType()
    {
        foreach (MessageAuditType type in Enum.GetValues<MessageAuditType>())
        {
            Assert.IsTrue(
                Enum.TryParse<AuditSearchFilterAuditType>(type.ToString(), out _),
                $"MessageAuditType.{type} has no matching AuditSearchFilterAuditType member — " +
                "add it to api-spec.yaml and regenerate the NSwag client.");
        }
    }
}
