using Crm.Api.Entities;
using Crm.Api.Mapping;
using Microsoft.Data.SqlClient;
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

            db.Contacts.Add(input);
            await db.SaveChangesAsync();
            await publisher.Publish(ContactMapper.ToCreatedEvent(input));
            return Results.Created($"/api/contacts/{input.Id}", input);
        });

        group.MapPut("/{id:guid}", async (Guid id, Contact input, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();

            existing.AccountId = input.AccountId;
            existing.FirstName = input.FirstName;
            existing.LastName = input.LastName;
            existing.Email = input.Email;
            existing.Phone = input.Phone;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await publisher.Publish(ContactMapper.ToUpdatedEvent(existing));
            return Results.Ok(existing);
        });

        // Upsert called by the adapter when ERP originates a contact change.
        // Origin is set to "Erp" on insert and never changed on update — the
        // upsert endpoint is only reachable from Erp*Contact* event handlers.
        // The payload's ErpCustomerId is the ERP-side customer id; we resolve
        // it here to a local Account.Id via Accounts.ErpCustomerId so the CRM
        // contact ends up linked to the correct CRM account row (or null if
        // the matching account hasn't been synced yet).
        group.MapPut("/upsert/{id:guid}", UpsertContactAsync);

        // User-driven soft delete: marks IsDeleted=true and publishes CrmContactDeleted.
        group.MapDelete("/{id:guid}", async (Guid id, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Contacts.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.IsDeleted) return Results.Ok(existing);

            existing.IsDeleted = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await publisher.Publish(ContactMapper.ToDeletedEvent(existing));
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

    private static async Task<IResult> UpsertContactAsync(
        Guid id, ContactUpsertRequest req, CrmDbContext db, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                // Lock the key (or its missing-key range) until both the contact
                // and EF-generated audit rows commit. HTTP retries may overlap
                // even when Service Bus delivers this session in order.
                var contact = await db.Contacts.FromSqlInterpolated(
                    $"SELECT * FROM [dbo].[Contacts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
                    .SingleOrDefaultAsync(cancellationToken);
                Guid? resolvedAccountId = null;
                if (req.ErpCustomerId is { } erpId && erpId != Guid.Empty)
                {
                    var account = await db.Accounts.FirstOrDefaultAsync(a => a.ErpCustomerId == erpId, cancellationToken);
                    resolvedAccountId = account?.Id;
                }

                if (contact is null)
                {
                    contact = new Contact
                    {
                        Id = id,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Origin = req.Origin ?? "Erp",
                    };
                    db.Contacts.Add(contact);
                }
                else
                {
                    contact.UpdatedAt = DateTimeOffset.UtcNow;
                }
                contact.AccountId = resolvedAccountId;
                contact.FirstName = req.FirstName;
                contact.LastName = req.LastName;
                contact.Email = req.Email;
                contact.Phone = req.Phone;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Ok(contact);
            }
            catch (Exception exception) when (attempt < 2 && IsDeadlock(exception))
            {
                // The disposed transaction rolled back its audit rows too. A new
                // attempt must not reuse tracked entities or generated audits.
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private static bool IsDeadlock(Exception exception) =>
        exception is SqlException { Number: 1205 }
        or DbUpdateException { InnerException: SqlException { Number: 1205 } };
}

// Adapter-side upsert request shape. Carries ErpCustomerId (ERP's customer id);
// the API resolves it to a local Account.Id via Accounts.ErpCustomerId so the
// CRM contact is FK-linked to the correct local account row. Origin is only
// honored on insert (null keeps the historical "Erp" default); updates never
// rewrite an existing contact's origin.
public record ContactUpsertRequest(Guid? ErpCustomerId, string FirstName, string LastName, string? Email, string? Phone, string? Origin = null);
