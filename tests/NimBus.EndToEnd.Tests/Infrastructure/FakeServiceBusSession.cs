using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// In-memory IServiceBusSession that records Complete/DeadLetter/Abandon/Defer calls
/// and provides session state management without Azure Service Bus.
/// </summary>
internal sealed class FakeServiceBusSession : IServiceBusSession
{
    private SessionState _sessionState = new();

    public int CompletedCount { get; private set; }
    public int DeadLetteredCount { get; private set; }
    public int DeferredCount { get; private set; }

    public string? LastDeadLetterReason { get; private set; }
    public string? LastDeadLetterDescription { get; private set; }

    public bool WasCompleted => CompletedCount > 0;
    public bool WasDeadLettered => DeadLetteredCount > 0;
    public bool WasDeferred => DeferredCount > 0;

    public Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        CompletedCount++;
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(IServiceBusMessage message, string reason, string errorDescription, CancellationToken cancellationToken = default)
    {
        DeadLetteredCount++;
        LastDeadLetterReason = reason;
        LastDeadLetterDescription = errorDescription;
        return Task.CompletedTask;
    }

    public Task DeferAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        DeferredCount++;
        return Task.CompletedTask;
    }

    public Task<IServiceBusMessage> ReceiveDeferredMessageAsync(long nextSequenceNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IServiceBusMessage>(null!);
    }

    public Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default)
    {
        _sessionState = sessionState;
        return Task.CompletedTask;
    }

    public Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState);
    }

    public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
