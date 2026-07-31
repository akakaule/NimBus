using Dapper;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IEventSchemaStore"/>. Carved out of
/// <see cref="SqlServerMessageStore"/>, which supplies connection/quoting/timeout
/// via <see cref="SqlServerStoreContext"/>.
/// </summary>
internal sealed class SqlServerEventSchemaStore : IEventSchemaStore
{
    private readonly SqlServerStoreContext _ctx;

    public SqlServerEventSchemaStore(SqlServerStoreContext ctx)
    {
        _ctx = ctx;
    }

    private string T(string table) => _ctx.Table(table);

    public async Task<EventSchema?> GetSchema(string eventTypeId)
    {
        await using var conn = await _ctx.Open();
        return await conn.QuerySingleOrDefaultAsync<EventSchema>(
            $"SELECT * FROM {T("EventSchemas")} WHERE [EventTypeId] = @eventTypeId",
            new { eventTypeId },
            commandTimeout: _ctx.CommandTimeout);
    }

    public async Task<IReadOnlyList<EventSchema>> GetSchemas()
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<EventSchema>(
            $"SELECT * FROM {T("EventSchemas")}",
            commandTimeout: _ctx.CommandTimeout);
        return rows.ToList();
    }

    public async Task<EventSchema> DefineEventType(EventSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema?.EventTypeId))
            throw new ArgumentException("schema.EventTypeId is required.", nameof(schema));
        if (string.IsNullOrWhiteSpace(schema?.JsonSchema))
            throw new ArgumentException("schema.JsonSchema is required.", nameof(schema));

        var existing = await GetSchema(schema.EventTypeId);
        if (existing != null)
        {
            if (!SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return existing;
        }

        await using var conn = await _ctx.Open();
        try
        {
            await conn.ExecuteAsync(
                $@"INSERT INTO {T("EventSchemas")}
                   ([EventTypeId],[Name],[JsonSchema],[Description],[SessionKeyPath],[Version],[AgentId],[CreatedBy],[CreatedUtc])
                   VALUES (@EventTypeId,@Name,@JsonSchema,@Description,@SessionKeyPath,@Version,@AgentId,@CreatedBy,@CreatedUtc)",
                schema,
                commandTimeout: _ctx.CommandTimeout);
            return schema;
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // PK / unique violation from a concurrent insert race — re-read and validate
            var raced = await GetSchema(schema.EventTypeId);
            if (raced is null)
                throw new InvalidOperationException(
                    $"Event type '{schema.EventTypeId}' reported a unique violation but could not be re-read.");
            if (!SchemaJson.Equal(raced.JsonSchema, schema.JsonSchema))
                throw new SchemaConflictException(schema.EventTypeId);
            return raced;
        }
    }
}
