using NimBus.Core.Events;
using System.ComponentModel.DataAnnotations;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// Test event representing an order being placed.
/// </summary>
internal sealed class OrderPlaced : Event
{
    [Required]
    public string? OrderId { get; set; }

    public string? CustomerName { get; set; }

    public decimal Amount { get; set; }

    private readonly string _sessionId;

    public OrderPlaced() : this(Guid.NewGuid().ToString()) { }

    public OrderPlaced(string sessionId)
    {
        _sessionId = sessionId;
    }

    public override string GetSessionId() => _sessionId;
}

/// <summary>
/// Test event representing an order being cancelled.
/// </summary>
internal sealed class OrderCancelled : Event
{
    [Required]
    public string? OrderId { get; set; }

    public string? Reason { get; set; }

    private readonly string _sessionId;

    public OrderCancelled() : this(Guid.NewGuid().ToString()) { }

    public OrderCancelled(string sessionId)
    {
        _sessionId = sessionId;
    }

    public override string GetSessionId() => _sessionId;
}

/// <summary>
/// Test event with no registered handler.
/// </summary>
internal sealed class UnknownEvent : Event
{
    public string? Data { get; set; }
}

/// <summary>
/// Test event with validation — OrderId is required.
/// </summary>
internal sealed class InvalidEvent : Event
{
    [Required]
    public string? RequiredField { get; set; }
}
