namespace NimBus.Inbox.SqlServer;

/// <summary>
/// Configuration for <see cref="SqlServerInbox"/>.
/// </summary>
public sealed class SqlServerInboxOptions
{
    /// <summary>
    /// Gets or sets the SQL Server connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the schema containing the inbox table. Defaults to <c>nimbus</c>.
    /// </summary>
    public string Schema { get; set; } = "nimbus";

    /// <summary>
    /// Gets or sets the inbox table name. Defaults to <c>InboxMessages</c>.
    /// </summary>
    public string TableName { get; set; } = "InboxMessages";

    /// <summary>
    /// Gets or sets a value indicating whether data operations lazily ensure the table exists.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    internal string FullTableName => $"[{Schema}].[{TableName}]";
}
