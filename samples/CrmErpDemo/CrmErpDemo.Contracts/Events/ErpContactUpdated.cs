using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Contact is updated. CustomerId is the ERP customer id the contact belongs to; the receiving CRM resolves it to its local account id via Account.ErpCustomerId.")]
[SessionKey(nameof(ContactId))]
public class ErpContactUpdated : Event
{
    [Required]
    public Guid ContactId { get; set; }

    public Guid? CustomerId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    public string? Phone { get; set; }
}
