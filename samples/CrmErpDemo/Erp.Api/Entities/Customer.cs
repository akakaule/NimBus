namespace Erp.Api.Entities;

public class Customer
{
    public Guid Id { get; set; }
    public string CustomerNumber { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid? CrmAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // "Erp" if originated in this ERP (POST /api/customers) or "Crm" if upserted in
    // from a Crm*Account* event. Set once at insert time and never modified.
    public string Origin { get; set; } = string.Empty;

    // Soft delete: rows are kept after a delete so the audit trail is preserved.
    public bool IsDeleted { get; set; }
}
