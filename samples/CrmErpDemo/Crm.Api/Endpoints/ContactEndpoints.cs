using Crm.Api.Entities;
using Crm.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace Crm.Api.Endpoints;

public static class ContactEndpoints
{
    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        group.MapGet("/", async (CrmDbContext db) =>
            Results.Ok(await db.Contacts.OrderByDescending(c => c.CreatedAt).ToListAsync()));

        group.MapGet("/{id:guid}", async (Guid id, CrmDbContext db) =>
            await db.Contacts.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        group.MapPost("/", async (Contact input, CrmDbContext db, IPublisherClient publisher) =>
        {
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.Origin = "Crm";

            await OutboxScope.RunAsync(db, async () =>
            {
                db.Contacts.Add(input);
                await db.SaveChangesAsync();
                await publisher.Publish(ContactMapper.ToCreatedEvent(input));
            });
            return Results.Created($"/api/contacts/{input.Id}", input);
        });

        group.MapPut("/{id:guid}", async (Guid id, Contact input, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.AccountId = input.AccountId;
                existing.FirstName = input.FirstName;
                existing.LastName = input.LastName;
                existing.Email = input.Email;
                existing.Phone = input.Phone;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(ContactMapper.ToUpdatedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // Upsert called by the adapter when ERP originates a contact change.
        // Origin is set to "Erp" on insert and never changed on update — the
        // upsert endpoint is only reachable from Erp*Contact* event handlers.
        // The payload's ErpCustomerId is the ERP-side customer id; we resolve
        // it here to a local Account.Id via Accounts.ErpCustomerId so the CRM
        // contact ends up linked to the correct CRM account row (or null if
        // the matching account hasn't been synced yet).
        group.MapPut("/upsert/{id:guid}", async (Guid id, ContactUpsertRequest req, CrmDbContext db) =>
        {
            Guid? resolvedAccountId = null;
            if (req.ErpCustomerId is { } erpId && erpId != Guid.Empty)
            {
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.ErpCustomerId == erpId);
                resolvedAccountId = account?.Id;
            }

            var existing = await db.Contacts.FindAsync(id);
            if (existing is null)
            {
                var contact = new Contact
                {
                    Id = id,
                    AccountId = resolvedAccountId,
                    FirstName = req.FirstName,
                    LastName = req.LastName,
                    Email = req.Email,
                    Phone = req.Phone,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Origin = "Erp",
                };
                db.Contacts.Add(contact);
                await db.SaveChangesAsync();
                return Results.Ok(contact);
            }
            existing.AccountId = resolvedAccountId;
            existing.FirstName = req.FirstName;
            existing.LastName = req.LastName;
            existing.Email = req.Email;
            existing.Phone = req.Phone;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });

        // User-driven soft delete: marks IsDeleted=true and publishes CrmContactDeleted.
        group.MapDelete("/{id:guid}", async (Guid id, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.IsDeleted) return Results.Ok(existing);

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.IsDeleted = true;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(ContactMapper.ToDeletedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // Adapter-side propagation of an ErpContactDeleted event. No re-publish.
        group.MapPost("/{id:guid}/deleted", async (Guid id, CrmDbContext db) =>
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

// Adapter-side upsert request shape. Carries ErpCustomerId (ERP's customer id);
// the API resolves it to a local Account.Id via Accounts.ErpCustomerId so the
// CRM contact is FK-linked to the correct local account row.
public record ContactUpsertRequest(Guid? ErpCustomerId, string FirstName, string LastName, string? Email, string? Phone);
