using System.Data;
using Microsoft.Data.SqlClient;
using NimBus.Core.Inbox;

namespace NimBus.Inbox.SqlServer;

/// <summary>
/// SQL Server-backed inbox store with concurrency-safe, first-write-wins records
/// keyed by the (endpoint, message) pair.
/// </summary>
public sealed class SqlServerInbox : IInboxStore, IDisposable
{
    private const int EndpointIdMaxLength = 260;
    private const int MessageIdMaxLength = 512;
    private const int IdentityHashLength = 32;
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
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        await EnsureInitializedAsync(cancellationToken);

        // Lookups go through the exact identity hash, never NVARCHAR equality: SQL Server pads
        // NVARCHAR operands with trailing spaces for comparisons regardless of collation, which
        // would report the distinct message id "m1 " as processed after "m1" was recorded.
        var sql = $"SELECT TOP (1) 1 FROM {_options.FullTableName} WHERE [IdentityHash] = @IdentityHash;";
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@IdentityHash", SqlDbType.Binary, IdentityHashLength).Value =
            InboxIdentity.ComputeHash(endpointId, messageId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc />
    public async Task RecordProcessedAsync(
        string endpointId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(endpointId, messageId);
        await EnsureInitializedAsync(cancellationToken);

        var sql = $"INSERT INTO {_options.FullTableName} ([IdentityHash], [EndpointId], [MessageId], [CreatedAtUtc]) VALUES (@IdentityHash, @EndpointId, @MessageId, SYSUTCDATETIME());";
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@IdentityHash", SqlDbType.Binary, IdentityHashLength).Value =
            InboxIdentity.ComputeHash(endpointId, messageId);
        command.Parameters.Add("@EndpointId", SqlDbType.NVarChar, EndpointIdMaxLength).Value = endpointId;
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
        string endpointId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (endpointId.Length > EndpointIdMaxLength)
        {
            throw new ArgumentException(
                $"Endpoint identifiers cannot exceed {EndpointIdMaxLength} characters.",
                nameof(endpointId));
        }

        await EnsureInitializedAsync(cancellationToken);

        // READCOMMITTEDLOCK accompanies READPAST because READPAST alone is rejected under
        // READ_COMMITTED_SNAPSHOT (common on Azure SQL); the pair pins the row-lock semantics
        // READPAST requires regardless of the database's RCSI setting.
        // The DATALENGTH predicate closes SQL Server's trailing-space padding hole: NVARCHAR
        // equality pads the shorter operand even under BIN2, so "e1" would otherwise also
        // purge the distinct endpoint "e1 ".
        var sql = $"""
            WITH [ExpiredInboxMessages] AS
            (
                SELECT TOP (@BatchSize) [IdentityHash]
                FROM {_options.FullTableName} WITH (READPAST, READCOMMITTEDLOCK)
                WHERE [EndpointId] = @EndpointId
                    AND DATALENGTH([EndpointId]) = DATALENGTH(@EndpointId)
                    AND [CreatedAtUtc] < @OlderThan
                ORDER BY [CreatedAtUtc], [IdentityHash]
            )
            DELETE FROM [ExpiredInboxMessages];
            """;
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = PurgeBatchSize;
        command.Parameters.Add("@EndpointId", SqlDbType.NVarChar, EndpointIdMaxLength).Value = endpointId;
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

            // The key is the 32-byte identity hash, not the NVARCHAR pair: SQL Server pads
            // NVARCHAR operands with trailing spaces for equality and unique-key comparisons
            // even under a BIN2 collation, so "m1" and "m1 " would collide as raw key columns.
            // The hash (shared with the Cosmos document id derivation) is byte-exact for any
            // identifier content. The raw columns are retained for diagnostics only. The PK
            // stays NONCLUSTERED — a random hash would fragment a clustered index — and the
            // table clusters on CreatedAtUtc, which keeps the bounded purge a range scan.
            var sql = $"""
                IF SCHEMA_ID(@Schema) IS NULL
                    EXEC(N'CREATE SCHEMA [{_options.Schema}]');

                IF OBJECT_ID(@QualifiedTableName, N'U') IS NULL
                BEGIN
                    CREATE TABLE {_options.FullTableName}
                    (
                        [IdentityHash] BINARY(32) NOT NULL,
                        [EndpointId] NVARCHAR(260) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        [MessageId] NVARCHAR(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        [CreatedAtUtc] DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT [PK_{_options.TableName}] PRIMARY KEY NONCLUSTERED ([IdentityHash]),
                        INDEX [IX_{_options.TableName}_CreatedAtUtc] CLUSTERED ([CreatedAtUtc])
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

    private static void ValidateIdentity(string endpointId, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (endpointId.Length > EndpointIdMaxLength)
        {
            throw new ArgumentException(
                $"Endpoint identifiers cannot exceed {EndpointIdMaxLength} characters.",
                nameof(endpointId));
        }

        if (messageId.Length > MessageIdMaxLength)
        {
            throw new ArgumentException(
                $"Message identifiers cannot exceed {MessageIdMaxLength} characters.",
                nameof(messageId));
        }
    }

    private static void ValidateSqlIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 112 || !IsIdentifierStart(value[0]))
        {
            throw new ArgumentException(
                // 112 (not 128) so the derived PK_/IX_..._CreatedAtUtc names stay within the
                // 128-character SQL identifier limit.
                "SQL identifiers must be 1-112 ASCII letters, digits, or underscores and cannot start with a digit.",
                parameterName);
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                throw new ArgumentException(
                    "SQL identifiers must be 1-112 ASCII letters, digits, or underscores and cannot start with a digit.",
                    parameterName);
            }
        }
    }

    private static bool IsIdentifierStart(char character)
        => character == '_' || character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char character)
        => IsIdentifierStart(character) || character is >= '0' and <= '9';
}
