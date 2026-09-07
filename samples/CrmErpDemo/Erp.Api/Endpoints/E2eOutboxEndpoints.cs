using Erp.Api.Entities;
using Erp.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace Erp.Api.Endpoints;

/// <summary>Test probes mapped only beneath the authenticated E2E route group.</summary>
public static class E2eOutboxEndpoints
{
    /// <summary>Exposes session-scoped outbox evidence and an intentionally rolled-back transaction.</summary>
    public static void MapE2eOutboxControls(this RouteGroupBuilder group)
    {
        group.MapGet("/outbox/{session:guid}", async (Guid session, ErpDbContext db, CancellationToken ct) =>
        {
            var id = session.ToString();
            var pending = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [nimbus].[OutboxMessages] WHERE [SessionId] = {id} AND [DispatchedAtUtc] IS NULL").SingleAsync(ct);
            var total = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [nimbus].[OutboxMessages] WHERE [SessionId] = {id}").SingleAsync(ct);
            return Results.Ok(new { pending, total });
        });
        group.MapPost("/outbox/rollback", async (ErpDbContext db, IPublisherClient publisher, CancellationToken ct) =>
        {
            var entity = new Customer
            {
                Id = Guid.NewGuid(), CustomerNumber = $"E2E-{Guid.NewGuid():N}", LegalName = "E2E rollback",
                CountryCode = "DE", Origin = "Erp", CreatedAt = DateTimeOffset.UtcNow,
            };
            try
            {
                await OutboxScope.RunAsync(db, async () =>
                {
                    db.Customers.Add(entity);
                    await db.SaveChangesAsync(ct);
                    await publisher.Publish(CustomerMapper.ToCreatedEvent(entity));
                    throw new E2eRollbackException();
                }, ct);
            }
            catch (E2eRollbackException) { /* The real OutboxScope rolled back both writes. */ }
            return Results.Ok(new { entity.Id });
        });
    }

    private sealed class E2eRollbackException : Exception;
}
