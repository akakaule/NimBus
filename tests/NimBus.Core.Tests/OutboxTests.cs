#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

[TestClass]
public class OutboxSenderTests
{
    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_NullOutbox_Throws()
    {
        new OutboxSender(null);
    }

    [TestMethod]
    public async Task Send_SingleMessage_StoresInOutbox()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message);

        Assert.AreEqual(1, outbox.StoredMessages.Count);
        Assert.AreEqual("msg-1", outbox.StoredMessages[0].MessageId);
        Assert.AreEqual("session-1", outbox.StoredMessages[0].SessionId);
    }

    [TestMethod]
    public async Task Send_WithDelay_SetsEnqueueDelayMinutes()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message, messageEnqueueDelay: 5);

        Assert.AreEqual(5, outbox.StoredMessages[0].EnqueueDelayMinutes);
    }

    [TestMethod]
    public async Task Send_BatchMessages_StoresAllInOutbox()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var messages = new[]
        {
            CreateMessage("msg-1", "OrderPlaced", "session-1"),
            CreateMessage("msg-2", "OrderPlaced", "session-1"),
            CreateMessage("msg-3", "PaymentCaptured", "session-2"),
        };

        await sender.Send(messages);

        Assert.AreEqual(3, outbox.BatchStoredMessages.Count);
    }

    [TestMethod]
    public async Task Send_SerializesPayloadAsJson()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message);

        var stored = outbox.StoredMessages[0];
        Assert.IsFalse(string.IsNullOrEmpty(stored.Payload));
        var deserialized = JsonConvert.DeserializeObject<Message>(stored.Payload);
        Assert.AreEqual("msg-1", deserialized.MessageId);
    }

    [TestMethod]
    public async Task ScheduleMessage_SetsScheduledEnqueueTimeUtc()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");
        var scheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        var seq = await sender.ScheduleMessage(message, scheduledTime);

        Assert.AreEqual(0L, seq, "Outbox always returns 0 for sequence number");
        Assert.IsNotNull(outbox.StoredMessages[0].ScheduledEnqueueTimeUtc);
        var diff = Math.Abs((scheduledTime.UtcDateTime - outbox.StoredMessages[0].ScheduledEnqueueTimeUtc.Value).TotalSeconds);
        Assert.IsTrue(diff < 1, "ScheduledEnqueueTimeUtc should match within 1 second");
    }

    [TestMethod]
    [ExpectedException(typeof(NotSupportedException))]
    public async Task CancelScheduledMessage_ThrowsNotSupported()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);

        await sender.CancelScheduledMessage(42);
    }

    [TestMethod]
    public async Task Send_SetsCreatedAtUtc()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var before = DateTime.UtcNow;

        await sender.Send(CreateMessage("msg-1", "OrderPlaced", "s1"));

        var after = DateTime.UtcNow;
        var created = outbox.StoredMessages[0].CreatedAtUtc;
        Assert.IsTrue(created >= before && created <= after);
    }

    [TestMethod]
    public async Task Send_DispatchedAtUtcIsNull()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);

        await sender.Send(CreateMessage("msg-1", "OrderPlaced", "s1"));

        Assert.IsNull(outbox.StoredMessages[0].DispatchedAtUtc);
    }

    private static Message CreateMessage(string messageId, string eventTypeId, string sessionId) => new()
    {
        MessageId = messageId,
        EventTypeId = eventTypeId,
        SessionId = sessionId,
        CorrelationId = "corr-1",
        To = "TestEndpoint",
        EventId = "evt-1",
        MessageType = MessageType.EventRequest,
        OriginatingMessageId = "self",
        ParentMessageId = "self",
        From = "publisher",
        OriginatingFrom = "publisher",
        OriginalSessionId = sessionId,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" }
        }
    };
}

[TestClass]
public class OutboxDispatcherTests
{
    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_NullOutbox_Throws()
    {
        new OutboxDispatcher(null, new RecordingSender());
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_NullSender_Throws()
    {
        new OutboxDispatcher(new InMemoryOutbox(), null);
    }

    [TestMethod]
    public async Task DispatchPending_NoPending_ReturnsZero()
    {
        var outbox = new InMemoryOutbox();
        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, sender.SentMessages.Count);
    }

    [TestMethod]
    public async Task DispatchPending_SendsMessagesAndMarksDispatched()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-1",
            MessageId = "msg-1",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-1",
                To = "Test",
                SessionId = "s1",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            EnqueueDelayMinutes = 0,
            CreatedAtUtc = DateTime.UtcNow
        });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, sender.SentMessages.Count);
        Assert.IsTrue(outbox.DispatchedIds.Contains("out-1"));
    }

    [TestMethod]
    public async Task DispatchPending_ScheduledMessage_UsesScheduleMessage()
    {
        var scheduledTime = DateTime.UtcNow.AddHours(1);
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-1",
            MessageId = "msg-1",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-1",
                To = "Test",
                SessionId = "s1",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            ScheduledEnqueueTimeUtc = scheduledTime,
            EnqueueDelayMinutes = 0,
            CreatedAtUtc = DateTime.UtcNow
        });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, sender.ScheduledMessages.Count, "Should use ScheduleMessage for scheduled outbox entries");
    }

    [TestMethod]
    public async Task DispatchPending_FirstFailure_StopsDispatching()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage { Id = "out-1", MessageId = "msg-1", Payload = "{invalid json", CreatedAtUtc = DateTime.UtcNow });
        outbox.AddPending(new OutboxMessage { Id = "out-2", MessageId = "msg-2", Payload = "{invalid json", CreatedAtUtc = DateTime.UtcNow });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, count, "Should stop on first failure");
        Assert.AreEqual(0, outbox.DispatchedIds.Count);
    }

    [TestMethod]
    public async Task DispatchPending_RespectsBatchSize()
    {
        var outbox = new InMemoryOutbox();
        for (int i = 0; i < 10; i++)
        {
            outbox.AddPending(new OutboxMessage
            {
                Id = $"out-{i}",
                MessageId = $"msg-{i}",
                Payload = JsonConvert.SerializeObject(new Message
                {
                    MessageId = $"msg-{i}",
                    To = "Test",
                    SessionId = "s1",
                    MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
                }),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync(batchSize: 3);

        Assert.AreEqual(3, count);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────

file sealed class InMemoryOutbox : IOutbox
{
    public List<OutboxMessage> StoredMessages { get; } = new();
    public List<OutboxMessage> BatchStoredMessages { get; } = new();
    public List<string> DispatchedIds { get; } = new();

    private readonly List<OutboxMessage> _pending = new();

    public void AddPending(OutboxMessage msg) => _pending.Add(msg);

    public Task StoreAsync(OutboxMessage message, CancellationToken ct = default)
    {
        StoredMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default)
    {
        BatchStoredMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct = default)
    {
        var result = _pending.Take(batchSize).ToList();
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(result);
    }

    public Task MarkAsDispatchedAsync(string id, CancellationToken ct = default)
    {
        DispatchedIds.Add(id);
        return Task.CompletedTask;
    }

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        DispatchedIds.AddRange(ids);
        return Task.CompletedTask;
    }
}

file sealed class RecordingSender : ISender
{
    public List<IMessage> SentMessages { get; } = new();
    public List<(IMessage Message, DateTimeOffset ScheduledTime)> ScheduledMessages { get; } = new();
    public int LastDelay { get; private set; }

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SentMessages.Add(message);
        LastDelay = messageEnqueueDelay;
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default)
    {
        ScheduledMessages.Add((message, scheduledEnqueueTime));
        return Task.FromResult(42L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default) => Task.CompletedTask;
}
