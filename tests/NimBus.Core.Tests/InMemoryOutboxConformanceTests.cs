#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;
using NimBus.Testing.Conformance;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class InMemoryOutboxConformanceTests : OutboxConformanceTests
{
    private readonly ConformanceInMemoryOutbox _outbox = new();

    protected override Task<IOutbox> CreateOutboxAsync() => Task.FromResult<IOutbox>(_outbox);

    protected override Task<DateTime?> GetDispatchedAtUtcAsync(string id) =>
        Task.FromResult(_outbox.GetDispatchedAtUtc(id));

    protected override Task AdvanceDispatchClockAsync()
    {
        _outbox.AdvanceClock();
        return Task.CompletedTask;
    }
}

internal sealed class ConformanceInMemoryOutbox : IOutbox
{
    private readonly Dictionary<string, OutboxMessage> _messages = new(StringComparer.Ordinal);
    private DateTime _utcNow = new(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    public DateTime? GetDispatchedAtUtc(string id) => _messages[id].DispatchedAtUtc;

    public void AdvanceClock() => _utcNow = _utcNow.AddMinutes(1);

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Add(message.Id, message);
        return Task.CompletedTask;
    }

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            _messages.Add(message.Id, message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutboxMessage> pending = _messages.Values
            .Where(message => message.DispatchedAtUtc is null)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(batchSize)
            .ToList();
        return Task.FromResult(pending);
    }

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        _messages[id].DispatchedAtUtc ??= _utcNow;
        return Task.CompletedTask;
    }

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            _messages[id].DispatchedAtUtc ??= _utcNow;
        }

        return Task.CompletedTask;
    }
}
