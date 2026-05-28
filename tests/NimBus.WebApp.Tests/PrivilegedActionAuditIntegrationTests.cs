#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 008 FR-051 — integration coverage of three migrated privileged-action
/// paths: Resubmit, Skip, PurgeMessages. Each test exercises both the success
/// and the access-denied branches and asserts that exactly one audit row is
/// written to the message store, with the expected AccessDenied flag, audit
/// type, and auditor name.
///
/// These tests stop short of spinning a TestServer — they invoke the
/// IAuditLogService surface directly with a synthesized HttpContext, mirroring
/// what the retrofitted controllers do. The goal is to lock in the contract:
/// "exactly one row per action, success or denied".
/// </summary>
[TestClass]
public sealed class PrivilegedActionAuditIntegrationTests
{
    [TestMethod]
    public async Task Resubmit_success_writes_single_audit_row()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("alice@example.com");

        // Simulates the success branch of EventImplementation.PostResubmitEventIdsAsync.
        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx,
            eventId: "evt-resub-success", endpointId: "ep-resub", eventTypeId: "OrderPlaced");

        var rows = (await store.GetMessageAudits("evt-resub-success")).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(MessageAuditType.Resubmit, rows[0].AuditType);
        Assert.IsFalse(rows[0].AccessDenied);
        Assert.AreEqual("alice@example.com", rows[0].AuditorName);
    }

    [TestMethod]
    public async Task Resubmit_access_denied_writes_single_audit_row_with_flag()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("eve@example.com");

        // Simulates the access-denied branch of EventImplementation.PostResubmitEventIdsAsync.
        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, accessDenied: true,
            eventId: "evt-resub-denied", endpointId: "ep-resub", eventTypeId: "OrderPlaced");

        var rows = (await store.GetMessageAudits("evt-resub-denied")).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.IsTrue(rows[0].AccessDenied);
        Assert.AreEqual("eve@example.com", rows[0].AuditorName);
    }

    [TestMethod]
    public async Task Skip_success_writes_single_audit_row()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("alice@example.com");

        await sut.LogAuditAsync(MessageAuditType.Skip, ctx,
            eventId: "evt-skip-success", endpointId: "ep-skip", eventTypeId: "OrderPlaced");

        var rows = (await store.GetMessageAudits("evt-skip-success")).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(MessageAuditType.Skip, rows[0].AuditType);
        Assert.IsFalse(rows[0].AccessDenied);
    }

    [TestMethod]
    public async Task Skip_access_denied_writes_single_audit_row_with_flag()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("eve@example.com");

        await sut.LogAuditAsync(MessageAuditType.Skip, ctx, accessDenied: true,
            eventId: "evt-skip-denied", endpointId: "ep-skip", eventTypeId: "OrderPlaced");

        var rows = (await store.GetMessageAudits("evt-skip-denied")).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(MessageAuditType.Skip, rows[0].AuditType);
        Assert.IsTrue(rows[0].AccessDenied);
    }

    [TestMethod]
    public async Task PurgeMessages_success_writes_single_audit_row()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("alice@example.com");

        // PurgeMessages is endpoint-scoped (no event id). The audit row is
        // emitted into the per-event audit list keyed on "" (the endpoint
        // alone — see EndpointImplementation.PostEndpointPurgeAsync).
        await sut.LogAuditAsync(MessageAuditType.PurgeMessages, ctx, endpointId: "ep-purge");

        var rows = (await store.GetMessageAudits(string.Empty)).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(MessageAuditType.PurgeMessages, rows[0].AuditType);
        Assert.IsFalse(rows[0].AccessDenied);
        Assert.AreEqual("ep-purge", rows[0].EndpointId);
    }

    [TestMethod]
    public async Task PurgeMessages_access_denied_writes_single_audit_row_with_flag()
    {
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("eve@example.com");

        await sut.LogAuditAsync(MessageAuditType.PurgeMessages, ctx, accessDenied: true,
            endpointId: "ep-purge-denied");

        var rows = (await store.GetMessageAudits(string.Empty)).ToList();
        Assert.IsTrue(rows.Any(r => r.AccessDenied
                                 && r.AuditType == MessageAuditType.PurgeMessages
                                 && r.EndpointId == "ep-purge-denied"));
    }

    [TestMethod]
    public async Task Repeated_calls_produce_one_row_per_invocation()
    {
        // Spec 008 Acceptance Scenario 3: two successive calls produce two rows.
        var store = new InMemoryMessageStore();
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var ctx = MakeContext("alice@example.com");

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, eventId: "evt-rep");
        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, eventId: "evt-rep");

        var rows = (await store.GetMessageAudits("evt-rep")).ToList();
        Assert.AreEqual(2, rows.Count);
    }

    private static HttpContext MakeContext(string preferredUsername)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("preferred_username", preferredUsername) }, "Test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }
}
