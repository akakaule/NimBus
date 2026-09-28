#pragma warning disable CA1707, CA2007
using BusinessCentral.Api.Data;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;

namespace DynamicsBcDemo.Tests;

/// <summary>A clock that stands still.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A clock the test moves forward.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>BC number series without SQL sequences, starting where the SQL sequences start.</summary>
internal sealed class InMemoryNumberSeries : INumberSeries
{
    private readonly Dictionary<NumberSeriesKind, long> _next = new()
    {
        [NumberSeriesKind.Contact] = 101,
        [NumberSeriesKind.Customer] = 50,
        [NumberSeriesKind.SalesQuote] = 1002,
        [NumberSeriesKind.SalesOrder] = 1001,
    };

    public Task<string> NextAsync(NumberSeriesKind kind, CancellationToken cancellationToken = default)
    {
        var value = _next[kind];
        _next[kind] = value + (kind == NumberSeriesKind.Customer ? 10 : 1);
        return Task.FromResult(NumberFormats.Format(kind, value));
    }
}

/// <summary>Records what would have been published.</summary>
internal sealed class CapturingPublisher : IPublisherClient
{
    public List<(IEvent Event, string? SessionId, string? MessageId)> Published { get; } = [];

    public Task Publish(IEvent @event)
    {
        Published.Add((@event, @event.GetSessionId(), null));
        return Task.CompletedTask;
    }

    public Task Publish(IMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Publish(IEvent @event, string sessionId, string correlationId)
    {
        Published.Add((@event, sessionId, null));
        return Task.CompletedTask;
    }

    public Task Publish(IEvent @event, string sessionId, string correlationId, string? messageId)
    {
        Published.Add((@event, sessionId, messageId));
        return Task.CompletedTask;
    }

    public Task PublishBatch(IEnumerable<IEvent> events, string? correlationId = null) => Task.CompletedTask;

    public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events)
    {
        yield return events;
    }
}

/// <summary>Answers every request with a canned response (or throws what it is given).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
