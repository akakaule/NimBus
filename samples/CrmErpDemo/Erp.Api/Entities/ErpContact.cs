namespace Erp.Api.Entities;

public class ErpContact
{
    public Guid Id { get; set; }
    public Guid? CustomerId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // "Erp" if originated in this ERP (POST /api/contacts) or "Crm" if upserted in
    // from a Crm*Contact* event. Set once at insert time and never modified.
    public string Origin { get; set; } = string.Empty;

    // Soft delete: rows are kept after a delete so the audit trail is preserved.
    public bool IsDeleted { get; set; }
}
