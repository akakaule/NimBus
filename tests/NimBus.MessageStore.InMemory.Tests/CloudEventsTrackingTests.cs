#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

/// <summary>
/// AC15: CloudEvents attributes (id/source/type/subject) are persisted in the
/// message tracking/audit record and readable through the existing tracking read
/// path — verified against the in-memory store.
/// </summary>
[TestClass]
public sealed class CloudEventsTrackingTests
{
    [TestMethod]
    public async Task StoreMessageAudit_PreservesCloudEventAttributes()
    {
        var store = new InMemoryMessageStore();
        var audit = new MessageAuditEntity
        {
            AuditorName = "system",
            AuditTimestamp = new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc),
            AuditType = MessageAuditType.Comment,
            CloudEventId = "ce-123",
            CloudEventSource = "urn:customer:billing",
            CloudEventType = "InvoiceCreated",
            CloudEventSubject = "customer-42",
        };

        await store.StoreMessageAudit("evt-1", audit);

        var read = (await store.GetMessageAudits("evt-1")).Single();
        Assert.AreEqual("ce-123", read.CloudEventId);
        Assert.AreEqual("urn:customer:billing", read.CloudEventSource);
        Assert.AreEqual("InvoiceCreated", read.CloudEventType);
        Assert.AreEqual("customer-42", read.CloudEventSubject);
    }

    [TestMethod]
    public async Task StoreMessageAudit_NativeMessage_LeavesCloudEventAttributesNull()
    {
        var store = new InMemoryMessageStore();
        var audit = new MessageAuditEntity
        {
            AuditorName = "system",
            AuditTimestamp = new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc),
            AuditType = MessageAuditType.Comment,
        };

        await store.StoreMessageAudit("evt-2", audit);

        var read = (await store.GetMessageAudits("evt-2")).Single();
        Assert.IsNull(read.CloudEventId);
        Assert.IsNull(read.CloudEventSource);
        Assert.IsNull(read.CloudEventType);
        Assert.IsNull(read.CloudEventSubject);
    }
}
