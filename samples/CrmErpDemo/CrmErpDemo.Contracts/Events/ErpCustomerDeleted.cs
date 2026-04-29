using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is soft-deleted. Receiving systems should mark their corresponding row as deleted.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerDeleted : Event
{
    [Required]
    [Description("Session key. CRM account id when the customer is linked; falls back to ErpCustomerId.")]
    public Guid AccountId { get; set; }

    [Required]
    public Guid ErpCustomerId { get; set; }

    [Description("When the deletion occurred in ERP.")]
    public DateTimeOffset DeletedAt { get; set; }
}
