namespace Crm.Api.Entities;

public class Contact
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // "Crm" if originated in this CRM (POST /api/contacts) or "Erp" if upserted in
    // from an Erp*Contact* event. Set once at insert time and never modified.
    public string Origin { get; set; } = string.Empty;

    // Soft delete: rows are kept after a delete so the audit trail is preserved.
    public bool IsDeleted { get; set; }
}
