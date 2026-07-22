#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.SqlServer.Tests;

[TestClass]
public sealed class SqlServerInboxTests
{
    private const string TestConnectionString = "Server=localhost;Database=nimbus-tests;Integrated Security=true;TrustServerCertificate=true";

    [TestMethod]
    public void Options_use_safe_defaults()
    {
        var options = new SqlServerInboxOptions();

        Assert.AreEqual("nimbus", options.Schema);
        Assert.AreEqual("InboxMessages", options.TableName);
        Assert.IsTrue(options.AutoCreateTable);
        Assert.AreEqual("[nimbus].[InboxMessages]", options.FullTableName);
    }

    [TestMethod]
    public void Constructor_rejects_missing_connection_string()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => new SqlServerInbox(new SqlServerInboxOptions()));

        Assert.AreEqual("options", exception.ParamName);
    }

    [TestMethod]
    [DataRow("bad-name")]
    [DataRow("bad]")]
    [DataRow("bad;DROP TABLE InboxMessages")]
    [DataRow("--comment")]
    public void Constructor_rejects_unsafe_SQL_identifiers(string identifier)
    {
        var options = new SqlServerInboxOptions
        {
            ConnectionString = TestConnectionString,
            Schema = identifier,
        };

        var exception = Assert.ThrowsExactly<ArgumentException>(() => new SqlServerInbox(options));

        Assert.AreEqual("Schema", exception.ParamName);
    }

    [TestMethod]
    public void AddNimBusSqlServerInbox_registers_the_same_keyed_and_unkeyed_singleton_without_connecting()
    {
        var services = new ServiceCollection();

        services.AddNimBusSqlServerInbox(options =>
        {
            options.ConnectionString = TestConnectionString;
            options.AutoCreateTable = false;
        });

        using var provider = services.BuildServiceProvider();
        var unkeyed = provider.GetRequiredService<IInboxStore>();
        var keyed = provider.GetRequiredKeyedService<IInboxStore>(InboxStore.SqlServer);

        Assert.AreSame(unkeyed, keyed);
        Assert.IsInstanceOfType<SqlServerInbox>(unkeyed);
    }
}
