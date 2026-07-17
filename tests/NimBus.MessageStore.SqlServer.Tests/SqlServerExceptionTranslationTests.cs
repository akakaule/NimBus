#pragma warning disable CA1707, CA2007
using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerExceptionTranslationTests
{
    [TestMethod]
    public async Task TranslateAsync_wraps_deadlock_as_provider_neutral_transient_without_details()
    {
        var sqlException = CreateSqlException(1205, "server=secret-host;password=secret-password");

        var exception = await Assert.ThrowsExactlyAsync<StorageProviderTransientException>(
            () => SqlServerExceptionTranslation.TranslateAsync(() => Task.FromException(sqlException)));

        Assert.IsNull(exception.RetryAfter);
        Assert.IsFalse(exception.Message.Contains("secret-host", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("secret-password", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TranslateAsync_does_not_reclassify_non_transient_sql_error()
    {
        var sqlException = CreateSqlException(2627, "duplicate key");

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            () => SqlServerExceptionTranslation.TranslateAsync(() => Task.FromException(sqlException)));

        Assert.AreSame(sqlException, exception);
    }

    [TestMethod]
    public async Task TranslateAsync_of_T_wraps_timeout_during_deferred_result_consumption()
    {
        var sqlException = CreateSqlException(-2, "server=secret-host;command timeout");

        var exception = await Assert.ThrowsExactlyAsync<StorageProviderTransientException>(
            () => SqlServerExceptionTranslation.TranslateAsync(
                () => Task.FromException<int>(sqlException)));

        Assert.IsNull(exception.RetryAfter);
        Assert.IsFalse(exception.Message.Contains("secret-host", StringComparison.Ordinal));
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var error = (SqlError)Activator.CreateInstance(
            typeof(SqlError),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { number, (byte)0, (byte)0, "server", message, "procedure", 0, 0, null },
            culture: null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errors, new object[] { error });

        return (SqlException)Activator.CreateInstance(
            typeof(SqlException),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { message, errors, null, Guid.NewGuid() },
            culture: null)!;
    }
}
