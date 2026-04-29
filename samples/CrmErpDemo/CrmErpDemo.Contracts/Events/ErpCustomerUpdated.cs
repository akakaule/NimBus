using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is updated. Carries the editable fields plus the linkage ids.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerUpdated : Event
{
    [Required]
    [Description("Session key. CRM account id when the customer is linked to a CRM account; falls back to ErpCustomerId so the field is always populated.")]
    public Guid AccountId { get; set; }

    [Required]
    public Guid ErpCustomerId { get; set; }

    [Required]
    public string CustomerNumber { get; set; } = string.Empty;

    [Required]
    public string LegalName { get; set; } = string.Empty;

    public string? TaxId { get; set; }

    [Required]
    public string CountryCode { get; set; } = string.Empty;
}
