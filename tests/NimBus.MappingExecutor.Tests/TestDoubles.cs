#pragma warning disable CA1707, CA2007
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;

namespace NimBus.MappingExecutor.Tests;

/// <summary>Captures every <see cref="IMappingTargetPublisher.Publish"/> call for assertion.</summary>
internal sealed class CapturingPublisher : IMappingTargetPublisher
{
    public int Count { get; private set; }
    public string? LastEventTypeId { get; private set; }
    public string? LastPayload { get; private set; }
    public string? LastSessionId { get; private set; }

    public Task Publish(string targetEventTypeId, string payload, string sessionId, CancellationToken ct)
    {
        Count++;
        LastEventTypeId = targetEventTypeId;
        LastPayload = payload;
        LastSessionId = sessionId;
        return Task.CompletedTask;
    }
}

/// <summary>Captures every <see cref="IMappingParkSink.Park"/> call for assertion.</summary>
internal sealed class CapturingPark : IMappingParkSink
{
    public int Count { get; private set; }
    public string? LastReason { get; private set; }

    public Task Park(IMessageContext context, string reason, CancellationToken ct)
    {
        Count++;
        LastReason = reason;
        return Task.CompletedTask;
    }
}
