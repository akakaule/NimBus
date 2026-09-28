using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NimBus.Outbox.SqlServer;

namespace BusinessCentral.Api.Integration;

internal static class OutboxScope
{
    // Runs <paramref name="work"/> inside a single SqlConnection + SqlTransaction shared between
    // EF and the NimBus outbox. Sharing one physical connection keeps the business write and the
    // outbox rows atomic without escalating to MSDTC (which TransactionScope + a second
    // SqlConnection would force, and which Aspire's containerized SQL Server can't service).
    public static async Task RunAsync(DbContext db, Func<Task> work, CancellationToken cancellationToken = default)
    {
        var conn = (SqlConnection)db.Database.GetDbConnection();
        var openedHere = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await db.Database.UseTransactionAsync(tx, cancellationToken);
            using (SqlServerOutboxAmbientTransaction.Begin(conn, tx))
            {
                await work();
            }

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            await db.Database.UseTransactionAsync(null, cancellationToken);
            if (openedHere) await conn.CloseAsync();
        }
    }
}
