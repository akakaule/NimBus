using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace CloudEventsInterop.Contracts.Events
{
    /// <summary>
    /// Domain event published by <see cref="Endpoints.SalesEndpoint"/> and consumed by
    /// <see cref="Endpoints.InvoicingEndpoint"/>. This is the event the sample emits both as a
    /// native NimBus message (when CloudEvents is disabled) and as a CloudEvents 1.0 message
    /// (when the publisher opts in) -- see docs/cloudevents.md.
    /// </summary>
    [Description("Published when a new invoice is created for a customer.")]
    [SessionKey(nameof(InvoiceId))]
    public class InvoiceCreated : Event
    {
        public static readonly InvoiceCreated Example = new InvoiceCreated
        {
            InvoiceId = Guid.Parse("3f9a6e9c-3e0a-4c0e-9a55-2a6c9d9a1b7e"),
            CustomerId = Guid.Parse("9e6d2e3a-1c9a-4b4e-8f3f-2b7c6d3a5e9a"),
            Amount = 249.95m,
            CurrencyCode = "EUR",
        };

        [Required]
        [Description("The unique invoice identifier.")]
        public Guid InvoiceId { get; set; }

        [Required]
        [Description("The customer the invoice was issued to.")]
        public Guid CustomerId { get; set; }

        [Range(0.01, 1000000)]
        [Description("The invoice total.")]
        public decimal Amount { get; set; }

        [Required]
        [Description("The ISO currency code for the invoice total.")]
        public string CurrencyCode { get; set; } = string.Empty;
    }
}
