using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is soft-deleted. Receiving systems should mark their corresponding row as deleted.")]
[SessionKey(nameof(AccountId))]
public class CrmAccountDeleted : Event
{
    [Required]
    public Guid AccountId { get; set; }

    [Description("ERP customer id if the account was previously linked; null otherwise.")]
    public Guid? ErpCustomerId { get; set; }

    [Description("When the deletion occurred in CRM.")]
    public DateTimeOffset DeletedAt { get; set; }
}
