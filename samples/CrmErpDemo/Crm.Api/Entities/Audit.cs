namespace Crm.Api.Entities;

// Per-record audit row. One row per changed field on Updated, plus a single
// Created/Deleted summary row when the entity enters or leaves the system.
// Populated automatically by CrmDbContext.SaveChangesAsync — every endpoint
// and adapter-driven mutation produces audit rows for free.
public class Audit
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FieldName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Origin { get; set; }
}
