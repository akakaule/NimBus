using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit");

        // Latest audit rows first so the UI can render a top-down timeline
        // without re-sorting. EntityType is "Account" or "Contact" — see
        // CrmDbContext.ResolveEntity for the canonical names.
        group.MapGet("/{entityType}/{entityId:guid}", async (string entityType, Guid entityId, CrmDbContext db) =>
        {
            var rows = await db.Audits
                .AsNoTracking()
                .Where(a => a.EntityType == entityType && a.EntityId == entityId)
                .OrderByDescending(a => a.Timestamp)
                .ThenByDescending(a => a.Id)
                .ToListAsync();
            return Results.Ok(rows);
        });
    }
}
