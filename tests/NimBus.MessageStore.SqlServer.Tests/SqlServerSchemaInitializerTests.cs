#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerSchemaInitializerTests
{
    [TestMethod]
    public async Task VerifyOnly_on_empty_database_fails_fast_with_missing_artifacts()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => initializer.StartAsync(CancellationToken.None));

        StringAssert.Contains(ex.Message, "Missing artifacts");
        StringAssert.Contains(ex.Message, $"schema '{schema}'");
        StringAssert.Contains(ex.Message, $"[{schema}].[DbUpJournal]");
        StringAssert.Contains(ex.Message, $"[{schema}].[Messages]");
        StringAssert.Contains(ex.Message, $"[{schema}].[UnresolvedEvents]");
    }

    [TestMethod]
    public async Task AutoApply_can_run_twice_on_same_connection_as_no_op()
    {
        var schema = NewSchemaName();
        var first = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var second = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var verify = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        await verify.StartAsync(CancellationToken.None);
    }

    private static SqlServerSchemaInitializer CreateInitializer(string schema, SchemaProvisioningMode mode)
        => new(
            Options.Create(new SqlServerMessageStoreOptions
            {
                ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
                Schema = schema,
                ProvisioningMode = mode,
            }),
            NullLogger<SqlServerSchemaInitializer>.Instance);

    private static string NewSchemaName()
        => $"nimbus_test_{Guid.NewGuid():N}"[..24];
}
