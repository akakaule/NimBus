using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Request/reply request: CRM asks ERP for the customer's current credit standing. Published by CrmEndpoint via PublisherClient.Request; answered synchronously by the ERP adapter's request handler over the CrmEndpoint-reply subscription. Shares the account's session key, so the check queues FIFO behind in-flight traffic for the same account.")]
[SessionKey(nameof(AccountId))]
public class ErpCreditCheckRequested : Event
{
    [Required]
    [Description("The CRM account whose ERP credit standing is being checked.")]
    public Guid AccountId { get; set; }

    [Description("Free-text identity of the requesting operator, for demo logging only.")]
    public string? RequestedBy { get; set; }

    [Description("When the check was requested (requester clock).")]
    public DateTimeOffset RequestedAt { get; set; }
}
