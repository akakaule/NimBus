using Erp.Api.Entities;
using Erp.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace Erp.Api.Endpoints;

public static class ErpContactEndpoints
{
    public static void MapErpContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        group.MapGet("/", async (ErpDbContext db) =>
            Results.Ok(await db.Contacts.OrderByDescending(c => c.CreatedAt).ToListAsync()));

        group.MapGet("/{id:guid}", async (Guid id, ErpDbContext db) =>
            await db.Contacts.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        // ERP-originated create.
        group.MapPost("/", async (ErpContact input, ErpDbContext db, IPublisherClient publisher) =>
        {
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.Origin = "Erp";

            await OutboxScope.RunAsync(db, async () =>
            {
                db.Contacts.Add(input);
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToContactCreatedEvent(input));
            });
            return Results.Created($"/api/contacts/{input.Id}", input);
        });

        // Upsert used by the ERP adapter when CRM originates a contact.
        // Origin is set to "Crm" on insert and never changed on update — the
        // upsert endpoint is only reachable from Crm*Contact* event handlers.
        // The payload's CrmAccountId is the CRM-side account id; we resolve it
        // here to a local Customer.Id via Customers.CrmAccountId so the ERP
        // contact ends up linked to the correct ERP customer row (or null if
        // the matching customer hasn't been synced yet).
        group.MapPut("/upsert/{id:guid}", async (Guid id, ErpContactUpsertRequest req, ErpDbContext db) =>
        {
            Guid? resolvedCustomerId = null;
            if (req.CrmAccountId is { } crmId && crmId != Guid.Empty)
            {
                var customer = await db.Customers.FirstOrDefaultAsync(c => c.CrmAccountId == crmId);
                resolvedCustomerId = customer?.Id;
            }

            var existing = await db.Contacts.FindAsync(id);
            if (existing is null)
            {
                var contact = new ErpContact
                {
                    Id = id,
                    CustomerId = resolvedCustomerId,
                    FirstName = req.FirstName,
                    LastName = req.LastName,
                    Email = req.Email,
                    Phone = req.Phone,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Origin = "Crm",
                };
                db.Contacts.Add(contact);
                await db.SaveChangesAsync();
                return Results.Ok(contact);
            }
            existing.CustomerId = resolvedCustomerId;
            existing.FirstName = req.FirstName;
            existing.LastName = req.LastName;
            existing.Email = req.Email;
            existing.Phone = req.Phone;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });

        // User-driven edit. Publishes ErpContactUpdated.
        group.MapPut("/{id:guid}", async (Guid id, ErpContact input, ErpDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.CustomerId = input.CustomerId;
                existing.FirstName = input.FirstName;
                existing.LastName = input.LastName;
                existing.Email = input.Email;
                existing.Phone = input.Phone;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToContactUpdatedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // User-driven soft delete: marks IsDeleted=true and publishes ErpContactDeleted.
        group.MapDelete("/{id:guid}", async (Guid id, ErpDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.IsDeleted) return Results.Ok(existing);

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.IsDeleted = true;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToContactDeletedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // Adapter-side propagation of a CrmContactDeleted event. No re-publish.
        group.MapPost("/{id:guid}/deleted", async (Guid id, ErpDbContext db) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.Ok();
            if (existing.IsDeleted) return Results.Ok(existing);
            existing.IsDeleted = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });
    }
}

// Adapter-side upsert request shape. Carries CrmAccountId (CRM's account id);
// the API resolves it to a local Customer.Id via Customers.CrmAccountId so the
// ERP contact is FK-linked to the correct local customer row.
public record ErpContactUpsertRequest(Guid? CrmAccountId, string FirstName, string LastName, string? Email, string? Phone);
