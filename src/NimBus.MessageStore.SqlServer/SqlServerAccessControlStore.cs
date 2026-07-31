using Dapper;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IAccessControlStore"/> (spec 026).
/// Carved out of <see cref="SqlServerMessageStore"/>, which supplies
/// connection/quoting/timeout via <see cref="SqlServerStoreContext"/>.
/// </summary>
internal sealed class SqlServerAccessControlStore : IAccessControlStore
{
    private readonly SqlServerStoreContext _ctx;

    public SqlServerAccessControlStore(SqlServerStoreContext ctx)
    {
        _ctx = ctx;
    }

    private string T(string table) => _ctx.Table(table);

    public Task<AccessControlList?> GetSiteAccessControl()
        => GetAccessControlRow(AccessControlList.SiteId);

    public Task SetSiteAccessControl(AccessControlList accessControl)
    {
        accessControl.Id = AccessControlList.SiteId;
        accessControl.EndpointId = null;
        return UpsertAccessControlRow(accessControl);
    }

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId)
        => GetAccessControlRow(AccessControlList.IdForEndpoint(endpointId));

    public async Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls()
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<string>(
            $"SELECT [ContentJson] FROM {T("AccessControl")} WHERE [Id] LIKE @prefix + '%'",
            new { prefix = AccessControlList.EndpointIdPrefix },
            commandTimeout: _ctx.CommandTimeout);
        return rows
            .Select(json => JsonConvert.DeserializeObject<AccessControlList>(json))
            .Where(acl => acl != null)
            .Select(acl => acl!)
            .ToList();
    }

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl)
    {
        accessControl.Id = AccessControlList.IdForEndpoint(endpointId);
        accessControl.EndpointId = endpointId;
        return UpsertAccessControlRow(accessControl);
    }

    private async Task<AccessControlList?> GetAccessControlRow(string id)
    {
        await using var conn = await _ctx.Open();
        var json = await conn.QuerySingleOrDefaultAsync<string>(
            $"SELECT [ContentJson] FROM {T("AccessControl")} WHERE [Id] = @id",
            new { id },
            commandTimeout: _ctx.CommandTimeout);
        return json == null ? null : JsonConvert.DeserializeObject<AccessControlList>(json);
    }

    private async Task UpsertAccessControlRow(AccessControlList accessControl)
    {
        await using var conn = await _ctx.Open();
        await conn.ExecuteAsync(
            $@"MERGE {T("AccessControl")} AS target
               USING (SELECT @Id AS [Id]) AS source
               ON target.[Id] = source.[Id]
               WHEN MATCHED THEN
                   UPDATE SET [ContentJson] = @ContentJson, [UpdatedAtUtc] = @UpdatedAtUtc
               WHEN NOT MATCHED THEN
                   INSERT ([Id], [ContentJson], [UpdatedAtUtc])
                   VALUES (@Id, @ContentJson, @UpdatedAtUtc);",
            new
            {
                accessControl.Id,
                ContentJson = JsonConvert.SerializeObject(accessControl),
                accessControl.UpdatedAtUtc,
            },
            commandTimeout: _ctx.CommandTimeout);
    }
}
