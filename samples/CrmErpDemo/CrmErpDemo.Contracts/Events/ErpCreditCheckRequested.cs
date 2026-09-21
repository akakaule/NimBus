using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Request/reply request: CRM asks ERP for the customer's current credit standing. Published by CrmEndpoint via PublisherClient.Request; answered synchronously by the ERP adapter's request handler over the CrmEndpoint-reply subscription. Shares the account's session key, so the check queues FIFO behind in-flight traffic for the same account.")]
[SessionKey(nameof(AccountId))]
public class ErpCreditCheckRequested : Event
{
    public static readonly ErpCreditCheckRequested Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        RequestedBy = "kirsten.madsen@contoso.example",
        RequestedAt = new DateTimeOffset(2026, 9, 21, 9, 45, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account whose ERP credit standing is being checked.")]
    public Guid AccountId { get; set; }

    // Free text that in practice carries an operator's name or sign-in, so it is
    // redacted wholesale rather than trusted to stay impersonal.
    [Sensitive]
    [Description("Free-text identity of the requesting operator, for demo logging only.")]
    public string? RequestedBy { get; set; }

    [Description("When the check was requested (requester clock).")]
    public DateTimeOffset RequestedAt { get; set; }
}
