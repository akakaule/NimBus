using Erp.Api.Entities;
using Erp.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace Erp.Api.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers");

        group.MapGet("/", async (ErpDbContext db) =>
            Results.Ok(await db.Customers.OrderByDescending(c => c.CreatedAt).ToListAsync()));

        group.MapGet("/{id:guid}", async (Guid id, ErpDbContext db) =>
            await db.Customers.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        // ERP-originated create.
        group.MapPost("/", async (Customer input, ErpDbContext db, IPublisherClient publisher) =>
        {
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CustomerNumber = string.IsNullOrWhiteSpace(input.CustomerNumber)
                ? $"C-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(10000, 99999)}"
                : input.CustomerNumber;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.Origin = "Erp";

            await OutboxScope.RunAsync(db, async () =>
            {
                db.Customers.Add(input);
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToCreatedEvent(input));
            });
            return Results.Created($"/api/customers/{input.Id}", input);
        });

        // Upsert used by the ERP adapter when CRM originates an account.
        // Idempotent on CrmAccountId: repeated events for the same account are no-ops.
        // Origin is set to "Crm" on insert and never changed on update — the
        // upsert endpoint is only reachable from CrmAccount* event handlers.
        // When isNew, the entity insert and the outbox publish commit together;
        // the update branch doesn't publish so the scope is just a passthrough.
        group.MapPut("/by-crm/{crmAccountId:guid}", async (Guid crmAccountId, CustomerUpsertRequest req, ErpDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Customers.FirstOrDefaultAsync(c => c.CrmAccountId == crmAccountId);
            var isNew = existing is null;
            Customer entity = existing!;

            await OutboxScope.RunAsync(db, async () =>
            {
                if (isNew)
                {
                    entity = new Customer
                    {
                        Id = Guid.NewGuid(),
                        CrmAccountId = crmAccountId,
                        CustomerNumber = $"C-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(10000, 99999)}",
                        LegalName = req.LegalName,
                        TaxId = req.TaxId,
                        CountryCode = req.CountryCode,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Origin = "Crm",
                    };
                    db.Customers.Add(entity);
                }
                else
                {
                    entity.LegalName = req.LegalName;
                    entity.TaxId = req.TaxId;
                    entity.CountryCode = req.CountryCode;
                    entity.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync();
                if (isNew)
                    await publisher.Publish(CustomerMapper.ToCreatedEvent(entity));
            });
            return Results.Ok(entity);
        });

        // User-driven edit. Only LegalName, TaxId, CountryCode are editable —
        // the customer number and CRM linkage stay stable. Publishes ErpCustomerUpdated.
        group.MapPut("/{id:guid}", async (Guid id, CustomerUpsertRequest req, ErpDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Customers.FindAsync(id);
            if (existing is null) return Results.NotFound();

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.LegalName = req.LegalName;
                existing.TaxId = req.TaxId;
                existing.CountryCode = req.CountryCode;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToUpdatedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // User-driven soft delete: marks IsDeleted=true and publishes ErpCustomerDeleted.
        group.MapDelete("/{id:guid}", async (Guid id, ErpDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Customers.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.IsDeleted) return Results.Ok(existing);

            await OutboxScope.RunAsync(db, async () =>
            {
                existing.IsDeleted = true;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await publisher.Publish(CustomerMapper.ToDeletedEvent(existing));
            });
            return Results.Ok(existing);
        });

        // Adapter-side propagation of a CrmAccountDeleted event. Marks the matching
        // ERP customer (by CrmAccountId) as deleted; no re-publish.
        group.MapPost("/by-crm/{crmAccountId:guid}/deleted", async (Guid crmAccountId, ErpDbContext db) =>
        {
            var existing = await db.Customers.FirstOrDefaultAsync(c => c.CrmAccountId == crmAccountId);
            if (existing is null) return Results.Ok();
            if (existing.IsDeleted) return Results.Ok(existing);
            existing.IsDeleted = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });
    }
}

public record CustomerUpsertRequest(string LegalName, string? TaxId, string CountryCode);
