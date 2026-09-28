using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessCentral.Api.Data;

/// <summary>BC-style number series for the documents and records the simulator creates.</summary>
public enum NumberSeriesKind
{
    Contact,
    Customer,
    SalesQuote,
    SalesOrder,
}

/// <summary>Hands out the next number of a series (BC's No. Series).</summary>
public interface INumberSeries
{
    Task<string> NextAsync(NumberSeriesKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Number formats shared by the SQL series and test doubles.</summary>
public static class NumberFormats
{
    public static string Format(NumberSeriesKind kind, long value) => kind switch
    {
        NumberSeriesKind.Contact => $"CT{value:000000}",
        NumberSeriesKind.Customer => $"C{value:00000}",
        NumberSeriesKind.SalesQuote => $"S-QUO{value}",
        NumberSeriesKind.SalesOrder => $"S-ORD{value}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// Number series backed by SQL SEQUENCEs, so concurrent inserts (a burst of new prospects from the whole
/// pilot office) never collide. Sequences are created by <see cref="BcDatabaseInitializer"/>.
/// </summary>
public sealed class SqlNumberSeries(BcDbContext db) : INumberSeries
{
    public async Task<string> NextAsync(NumberSeriesKind kind, CancellationToken cancellationToken = default)
    {
        // Fixed statements per series; NEXT VALUE FOR is not allowed in a derived table, so this
        // must not go through a composed EF query.
        var sql = kind switch
        {
            NumberSeriesKind.Contact => "SELECT NEXT VALUE FOR dbo.ContactNo",
            NumberSeriesKind.Customer => "SELECT NEXT VALUE FOR dbo.CustomerNo",
            NumberSeriesKind.SalesQuote => "SELECT NEXT VALUE FOR dbo.SalesQuoteNo",
            NumberSeriesKind.SalesOrder => "SELECT NEXT VALUE FOR dbo.SalesOrderNo",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return NumberFormats.Format(kind, value);
    }
}
