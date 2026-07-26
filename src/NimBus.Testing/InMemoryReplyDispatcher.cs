using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

/// <summary>
/// In-memory <see cref="IReplyDispatcher"/> that captures request/reply responses
/// instead of sending them to Service Bus. Assert on <see cref="SentReplies"/>.
/// </summary>
public sealed class InMemoryReplyDispatcher : IReplyDispatcher
{
    private readonly List<ReplyMessage> _sentReplies = new();
    private readonly object _lock = new();

    /// <summary>Every reply sent through this dispatcher, in send order.</summary>
    public IReadOnlyList<ReplyMessage> SentReplies
    {
        get
        {
            lock (_lock)
            {
                return _sentReplies.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task SendReplyAsync(ReplyMessage reply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        lock (_lock)
        {
            _sentReplies.Add(reply);
        }

        return Task.CompletedTask;
    }
}
