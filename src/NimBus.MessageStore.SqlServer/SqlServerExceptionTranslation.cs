using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Translates retryable SQL Server failures at the provider boundary so callers
/// can apply provider-neutral retry policy without receiving database details.
/// </summary>
internal static class SqlServerExceptionTranslation
{
    private const string TransientFailureMessage = "SQL Server message store is temporarily unavailable.";

    public static async Task<T> TranslateAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsTransient(ex))
        {
            throw new StorageProviderTransientException(TransientFailureMessage, retryAfter: null);
        }
    }

    public static async Task TranslateAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsTransient(ex))
        {
            throw new StorageProviderTransientException(TransientFailureMessage, retryAfter: null);
        }
    }

    internal static bool IsTransient(SqlException exception) =>
        exception.IsTransient || exception.Number is
            -2 or      // Command timeout.
            233 or     // Connection initialization failure.
            1205 or    // Deadlock victim.
            10928 or 10929 or
            40197 or 40501 or 40613 or
            49918 or 49919 or 49920 or
            10053 or 10054 or 10060;
}
