using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace DynamicsBcDemo.Contracts.D365Sales;

// Deliberately no [SessionKey]: the request is itself an audited message, and a read-only
// question needs no ordering. Sharing the customer's session would let a failed or slow check
// block every other message about that customer.
[Description("Request/reply: a seller asks Business Central for a customer's live credit status (credit limit, balance, blocked). Answered synchronously with a BcCreditStatus reply on the D365SalesEndpoint-reply subscription. Has no session key, so a check can never block the customer's other traffic.")]
public class D365CreditCheckRequested : Event
{
    public static readonly D365CreditCheckRequested Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000101"),
        BcCustomerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0010"),
        BcCustomerNumber = "C00010",
        RequestedBy = "alex.rivera@contososubsea.example",
        RequestedAt = new DateTimeOffset(2026, 9, 28, 9, 45, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account the check is for.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The BC customer id (customers resource) linked to the account.")]
    public Guid BcCustomerId { get; set; }

    [Description("The BC customer number, for logs and the reply.")]
    public string? BcCustomerNumber { get; set; }

    [Sensitive]
    [Description("Who asked (seller e-mail), for demo logging only.")]
    public string? RequestedBy { get; set; }

    [Description("When the check was requested (CRM clock).")]
    public DateTimeOffset RequestedAt { get; set; }
}
