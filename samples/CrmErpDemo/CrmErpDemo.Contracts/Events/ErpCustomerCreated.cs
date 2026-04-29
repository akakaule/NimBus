using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is created. Origin distinguishes the CRM-originated round-trip ack from a customer that originated directly in ERP. Session-keyed on AccountId so per-customer ordering is preserved.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerCreated : Event
{
    [Required]
    [Description("Where the customer originated. Crm = ack of a CRM-originated CrmAccountCreated round-trip; Erp = customer created directly in ERP.")]
    public CustomerOrigin Origin { get; set; }

    [Required]
    [Description("Session key for per-customer ordering. When Origin = Crm this is the CRM account id; when Origin = Erp it falls back to ErpCustomerId so the field is still a stable key.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The ERP-side customer identifier.")]
    public Guid ErpCustomerId { get; set; }

    [Required]
    [Description("The ERP customer number (human-readable reference).")]
    public string CustomerNumber { get; set; } = string.Empty;

    [Required]
    public string LegalName { get; set; } = string.Empty;

    public string? TaxId { get; set; }

    [Required]
    public string CountryCode { get; set; } = string.Empty;
}

public enum CustomerOrigin
{
    Erp = 0,
    Crm = 1,
}
