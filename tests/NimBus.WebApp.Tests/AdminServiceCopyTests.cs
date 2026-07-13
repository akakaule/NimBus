#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class AdminServiceCopyTests
{
    private static readonly string[] ExpectedEventIds = { "event-1", "event-2" };
    private static readonly string[] ExpectedMessageIds = { "message-1", "message-2" };
    private static readonly string[] ExpectedFirstDocumentId = { "event-1-doc" };

    [TestMethod]
    public async Task Copy_batch_collects_copied_event_ids_and_copies_only_related_messages()
    {
        var copiedEventIds = new HashSet<string>(StringComparer.Ordinal);
        var copiedEvents = new List<JObject>();
        var events = new[]
        {
            EventDocument("event-1"),
            EventDocument("event-2"),
        };

        var eventCount = await AdminService.CopyDocumentBatchAsync(
            events,
            doc =>
            {
                copiedEvents.Add(doc);
                return Task.CompletedTask;
            },
            doc => copiedEventIds.Add(doc["event"]!["EventId"]!.ToString()),
            batchSize: null,
            eventIdFilter: null);

        var copiedMessages = new List<JObject>();
        var messages = new[]
        {
            MessageDocument("message-1", "event-1"),
            MessageDocument("message-unrelated", "event-other"),
            MessageDocument("message-2", "event-2"),
        };

        var messageCount = await AdminService.CopyDocumentBatchAsync(
            messages,
            doc =>
            {
                copiedMessages.Add(doc);
                return Task.CompletedTask;
            },
            onDocumentCopied: null,
            batchSize: null,
            eventIdFilter: copiedEventIds);

        Assert.AreEqual(2, eventCount);
        CollectionAssert.AreEquivalent(ExpectedEventIds, copiedEventIds.ToArray());
        Assert.AreEqual(2, messageCount);
        CollectionAssert.AreEquivalent(
            ExpectedMessageIds,
            copiedMessages.Select(doc => doc["id"]!.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Copy_batch_honors_limit_and_invokes_callback_only_after_successful_upsert()
    {
        var callbacks = new List<string>();
        var upserts = new List<string>();
        var documents = new[]
        {
            EventDocument("event-1"),
            EventDocument("event-2"),
        };

        var count = await AdminService.CopyDocumentBatchAsync(
            documents,
            doc =>
            {
                upserts.Add(doc["id"]!.ToString());
                return Task.CompletedTask;
            },
            doc => callbacks.Add(doc["id"]!.ToString()),
            batchSize: 1,
            eventIdFilter: null);

        Assert.AreEqual(1, count);
        CollectionAssert.AreEqual(ExpectedFirstDocumentId, upserts);
        CollectionAssert.AreEqual(ExpectedFirstDocumentId, callbacks);
        Assert.IsNull(documents[0]["ttl"]);
        Assert.IsNotNull(documents[1]["ttl"]);
    }

    [TestMethod]
    public async Task Copy_batch_does_not_collect_event_id_when_upsert_fails()
    {
        var callbackInvoked = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => AdminService.CopyDocumentBatchAsync(
                new[] { EventDocument("event-1") },
                _ => throw new InvalidOperationException("upsert failed"),
                _ => callbackInvoked = true,
                batchSize: null,
                eventIdFilter: null));

        Assert.IsFalse(callbackInvoked);
    }

    private static JObject EventDocument(string eventId) =>
        new()
        {
            ["id"] = eventId + "-doc",
            ["ttl"] = 60,
            ["event"] = new JObject { ["EventId"] = eventId },
        };

    private static JObject MessageDocument(string id, string eventId) =>
        new()
        {
            ["id"] = id,
            ["eventId"] = eventId,
            ["ttl"] = 60,
        };
}
