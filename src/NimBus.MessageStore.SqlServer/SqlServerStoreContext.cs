using Microsoft.Data.SqlClient;
using System;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Shared plumbing handed from <see cref="SqlServerMessageStore"/> to the carved-out
/// concern stores: connection opening (with exception translation), defensive
/// bracket-quoting of table names, and the configured command timeout.
/// </summary>
internal sealed class SqlServerStoreContext
{
    public required Func<Task<SqlConnection>> Open { get; init; }

    public required Func<string, string> Table { get; init; }

    public required int CommandTimeout { get; init; }
}
