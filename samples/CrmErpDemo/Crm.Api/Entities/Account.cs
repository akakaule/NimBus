namespace Crm.Api.Entities;

public class Account
{
    public Guid Id { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid? ErpCustomerId { get; set; }
    public string? ErpCustomerNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // "Crm" if originated in this CRM (POST /api/accounts) or "Erp" if upserted in
    // from an Erp*Customer* event. Set once at insert time and never modified.
    public string Origin { get; set; } = string.Empty;

    // Soft delete: rows are kept after a delete so the audit trail is preserved.
    // Toggled by the local DELETE endpoint (which also publishes a CrmAccountDeleted)
    // and by the cross-side handler responding to an ErpCustomerDeleted event.
    public bool IsDeleted { get; set; }
}
