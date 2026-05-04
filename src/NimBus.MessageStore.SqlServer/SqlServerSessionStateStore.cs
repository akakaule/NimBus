using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server-backed implementation of <see cref="ISessionStateStore"/>. State
/// is held in <c>[schema].[SessionStates]</c> (created by migration
/// <c>0009_SessionStates.sql</c>). All writes are upserts on the natural key
/// <c>(EndpointId, SessionId)</c>.
/// </summary>
public sealed class SqlServerSessionStateStore : ISessionStateStore
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly string _schema;
    private readonly int _commandTimeout;

    public SqlServerSessionStateStore(IOptions<SqlServerMessageStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _schema = _options.Schema;
        _commandTimeout = _options.CommandTimeoutSeconds;
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_options.ConnectionString);
        conn.Open();
        return conn;
    }

    private string T(string table) => $"[{_schema}].[{table}]";

    public async Task BlockSession(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
MERGE {T("SessionStates")} AS target
USING (SELECT @EndpointId AS EndpointId, @SessionId AS SessionId) AS source
    ON target.EndpointId = source.EndpointId AND target.SessionId = source.SessionId
WHEN MATCHED THEN
    UPDATE SET BlockedByEventId = @EventId, UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (EndpointId, SessionId, BlockedByEventId)
    VALUES (@EndpointId, @SessionId, @EventId);";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId, EventId = eventId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UnblockSession(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
UPDATE {T("SessionStates")}
SET BlockedByEventId = NULL, UpdatedAtUtc = SYSUTCDATETIME()
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> IsSessionBlocked(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1
    CASE WHEN BlockedByEventId IS NOT NULL OR DeferredCount > 0 THEN 1 ELSE 0 END
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        var result = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result == 1;
    }

    public async Task<bool> IsSessionBlockedByThis(string endpointId, string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1 BlockedByEventId
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        var blockedBy = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return !string.IsNullOrEmpty(blockedBy)
            && string.Equals(blockedBy, eventId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsSessionBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1
    CASE WHEN BlockedByEventId IS NOT NULL THEN 1 ELSE 0 END
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        var result = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result == 1;
    }

    public async Task<string> GetBlockedByEventId(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1 BlockedByEventId
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? string.Empty;
    }

    public async Task<int> GetNextDeferralSequenceAndIncrement(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        // Atomic upsert-and-return: the previous NextDeferralSequence is the
        // value returned, and the row is left with NextDeferralSequence + 1.
        var sql = $@"
MERGE {T("SessionStates")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @SessionId AS SessionId) AS source
    ON target.EndpointId = source.EndpointId AND target.SessionId = source.SessionId
WHEN MATCHED THEN
    UPDATE SET NextDeferralSequence = target.NextDeferralSequence + 1, UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (EndpointId, SessionId, NextDeferralSequence) VALUES (@EndpointId, @SessionId, 1)
OUTPUT deleted.NextDeferralSequence;";

        await using var conn = Open();
        var previous = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return previous ?? 0;
    }

    public async Task IncrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
MERGE {T("SessionStates")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @SessionId AS SessionId) AS source
    ON target.EndpointId = source.EndpointId AND target.SessionId = source.SessionId
WHEN MATCHED THEN
    UPDATE SET DeferredCount = target.DeferredCount + 1, UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (EndpointId, SessionId, DeferredCount) VALUES (@EndpointId, @SessionId, 1);";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DecrementDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
UPDATE {T("SessionStates")}
SET DeferredCount = DeferredCount - 1, UpdatedAtUtc = SYSUTCDATETIME()
WHERE EndpointId = @EndpointId AND SessionId = @SessionId AND DeferredCount > 0;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> GetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1 DeferredCount
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        var result = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result ?? 0;
    }

    public async Task<bool> HasDeferredMessages(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT TOP 1
    CASE WHEN DeferredCount > 0 THEN 1 ELSE 0 END
FROM {T("SessionStates")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        var result = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result == 1;
    }

    public async Task ResetDeferredCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
UPDATE {T("SessionStates")}
SET DeferredCount = 0, UpdatedAtUtc = SYSUTCDATETIME()
WHERE EndpointId = @EndpointId AND SessionId = @SessionId;";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _commandTimeout, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // TODO: task #5 implementation — issue #20
    public Task<int> GetLastReplayedSequence(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    // TODO: task #5 implementation — issue #20
    public Task<bool> TryAdvanceLastReplayedSequence(string endpointId, string sessionId, int expectedCurrent, int newValue, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // TODO: task #5 implementation — issue #20
    public Task<int> GetActiveParkCount(string endpointId, string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
