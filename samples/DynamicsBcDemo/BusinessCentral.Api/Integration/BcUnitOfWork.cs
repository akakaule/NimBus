using BusinessCentral.Api.Data;
using BusinessCentral.Api.Domain;
using NimBus.SDK;

namespace BusinessCentral.Api.Integration;

/// <summary>
/// Runs one BC operation atomically with its events: the data change and the NimBus outbox rows
/// commit in one SQL transaction, and the outbox dispatcher sends them afterwards. In a real
/// tenant this is where BC's webhook → ingress → publish path sits; the contract events are the
/// same either way.
/// </summary>
public sealed class BcUnitOfWork(BcDbContext db, IPublisherClient publisher)
{
    public async Task<T> RunAsync<T>(Func<EventBuffer, Task<T>> work, CancellationToken cancellationToken = default)
    {
        T result = default!;
        await OutboxScope.RunAsync(db, async () =>
        {
            var events = new EventBuffer();
            result = await work(events);
            await db.SaveChangesAsync(cancellationToken);

            // One Publish per event, in order. The outbox dispatches oldest-first by creation
            // time, so sequential publishes keep e.g. customer-created ahead of order-created in
            // the customer's session. (A single batch insert would share one timestamp.)
            foreach (var @event in events.Events)
            {
                await publisher.Publish(@event);
            }
        }, cancellationToken);

        return result;
    }
}
