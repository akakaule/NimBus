using System.Data;
using Microsoft.Data.SqlClient;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.SqlServer;

/// <summary>
/// SQL Server-backed inbox store with concurrency-safe, first-write-wins records.
/// </summary>
public sealed class SqlServerInbox : IInboxStore, IDisposable
{
    private const int MessageIdMaxLength = 512;
    private const int PurgeBatchSize = 1_000;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SqlServerInboxOptions _options;
    private bool _tableEnsured;

    /// <summary>
    /// Initializes a new SQL Server inbox store.
    /// </summary>
    /// <param name="options">SQL connection and table configuration.</param>
    public SqlServerInbox(SqlServerInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("A SQL Server connection string is required.", nameof(options));
        }

        ValidateSqlIdentifier(options.Schema, nameof(options.Schema));
        ValidateSqlIdentifier(options.TableName, nameof(options.TableName));
        // Snapshot validated configuration so later mutation cannot bypass the
        // strict identifier guard used before identifiers enter SQL text.
        _options = new SqlServerInboxOptions
        {
            ConnectionString = options.ConnectionString,
            Schema = options.Schema,
            TableName = options.TableName,
            AutoCreateTable = options.AutoCreateTable,
        };
    }

    /// <summary>
    /// Idempotently creates the configured schema, inbox table, primary key, and purge index.
    /// Call this method explicitly when <see cref="SqlServerInboxOptions.AutoCreateTable"/>
    /// is disabled and schema provisioning is managed during deployment.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels schema initialization.</param>
    /// <returns>A task that completes after the table is available.</returns>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken = default)
        => EnsureTableExistsCoreAsync(force: true, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasProcessedAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMessageId(messageId);
        await EnsureInitializedAsync(cancellationToken);

        var sql = $"SELECT TOP (1) 1 FROM {_options.FullTableName} WHERE [MessageId] = @MessageId;";
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MessageId", SqlDbType.NVarChar, MessageIdMaxLength).Value = messageId;

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc />
    public async Task RecordProcessedAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMessageId(messageId);
        await EnsureInitializedAsync(cancellationToken);

        var sql = $"INSERT INTO {_options.FullTableName} ([MessageId], [CreatedAtUtc]) VALUES (@MessageId, SYSUTCDATETIME());";
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MessageId", SqlDbType.NVarChar, MessageIdMaxLength).Value = messageId;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // A concurrent first writer won. The existing CreatedAtUtc is retained.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        var sql = $"""
            WITH [ExpiredInboxMessages] AS
            (
                SELECT TOP (@BatchSize) [MessageId]
                FROM {_options.FullTableName} WITH (READPAST)
                WHERE [CreatedAtUtc] < @OlderThan
                ORDER BY [CreatedAtUtc], [MessageId]
            )
            DELETE FROM [ExpiredInboxMessages];
            """;
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = PurgeBatchSize;
        command.Parameters.Add("@OlderThan", SqlDbType.DateTime2).Value = olderThan.UtcDateTime;

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _initializationLock.Dispose();

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoCreateTable || Volatile.Read(ref _tableEnsured))
        {
            return;
        }

        await EnsureTableExistsCoreAsync(force: false, cancellationToken);
    }

    private async Task EnsureTableExistsCoreAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && Volatile.Read(ref _tableEnsured))
            {
                return;
            }

            var sql = $"""
                IF SCHEMA_ID(@Schema) IS NULL
                    EXEC(N'CREATE SCHEMA [{_options.Schema}]');

                IF OBJECT_ID(@QualifiedTableName, N'U') IS NULL
                BEGIN
                    CREATE TABLE {_options.FullTableName}
                    (
                        [MessageId] NVARCHAR(512) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                        [CreatedAtUtc] DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                        INDEX [IX_InboxMessages_CreatedAtUtc] NONCLUSTERED ([CreatedAtUtc])
                    );
                END;
                """;
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = _options.Schema;
            command.Parameters.Add("@QualifiedTableName", SqlDbType.NVarChar, 261).Value = _options.FullTableName;
            await command.ExecuteNonQueryAsync(cancellationToken);
            Volatile.Write(ref _tableEnsured, true);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static void ValidateMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (messageId.Length > MessageIdMaxLength)
        {
            throw new ArgumentException(
                $"Message identifiers cannot exceed {MessageIdMaxLength} characters.",
                nameof(messageId));
        }
    }

    private static void ValidateSqlIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IsIdentifierStart(value[0]))
        {
            throw new ArgumentException(
                "SQL identifiers must be 1-128 ASCII letters, digits, or underscores and cannot start with a digit.",
                parameterName);
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                throw new ArgumentException(
                    "SQL identifiers must be 1-128 ASCII letters, digits, or underscores and cannot start with a digit.",
                    parameterName);
            }
        }
    }

    private static bool IsIdentifierStart(char character)
        => character == '_' || character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char character)
        => IsIdentifierStart(character) || character is >= '0' and <= '9';
}
