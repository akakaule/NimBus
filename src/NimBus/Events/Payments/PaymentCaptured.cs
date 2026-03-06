using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Payments
{
    [Description("Published when payment has been successfully captured for an order.")]
    public class PaymentCaptured : Event
    {
        public static readonly PaymentCaptured Example = new PaymentCaptured
        {
            OrderId = Guid.Parse("2bb7d0b3-840f-4e54-a0d4-fb31a7cabf82"),
            PaymentId = Guid.Parse("34d38416-b3f7-445a-9b4b-566c0cf0f3d8"),
            Amount = 249.95m,
            CapturedAt = DateTime.UtcNow,
        };

        [Required]
        [Description("The order that the captured payment belongs to.")]
        public Guid OrderId { get; set; }

        [Required]
        [Description("The payment transaction identifier.")]
        public Guid PaymentId { get; set; }

        [Range(0.01, 1000000)]
        [Description("The captured amount.")]
        public decimal Amount { get; set; }

        [Description("The UTC timestamp when the payment was captured.")]
        public DateTime CapturedAt { get; set; }

        public override string GetSessionId() => OrderId.ToString();
    }
}
