using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Contact is created. CustomerId is the ERP customer id the contact belongs to; the receiving CRM resolves it to its local account id via Account.ErpCustomerId.")]
[SessionKey(nameof(ContactId))]
public class ErpContactCreated : Event
{
    [Required]
    public Guid ContactId { get; set; }

    [Description("The ERP customer this contact belongs to (ERP-side id). Receivers must resolve to their local FK if needed.")]
    public Guid? CustomerId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    public string? Phone { get; set; }
}
