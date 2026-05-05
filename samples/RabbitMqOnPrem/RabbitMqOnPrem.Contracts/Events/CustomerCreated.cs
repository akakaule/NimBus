using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace RabbitMqOnPrem.Contracts.Events;

/// <summary>
/// Demo event published by the Publisher and consumed by the Subscriber. The
/// <see cref="CustomerId"/> doubles as the session key so all events for a
/// given customer route to the same RabbitMQ partition queue and are processed
/// in send order (single-active-consumer per queue preserves FIFO).
/// </summary>
[Description("Published when a customer is created in the on-prem demo.")]
[SessionKey(nameof(CustomerId))]
public class CustomerCreated : Event
{
    [Required]
    [Description("The customer that was created (also the session key).")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("The customer's display name.")]
    public string Name { get; set; } = string.Empty;

    [Description("The customer's email address.")]
    public string Email { get; set; } = string.Empty;

    [Description("Set true to make the subscriber simulate a handler failure (for DLQ flow demos).")]
    public bool SimulateFailure { get; set; }
}
