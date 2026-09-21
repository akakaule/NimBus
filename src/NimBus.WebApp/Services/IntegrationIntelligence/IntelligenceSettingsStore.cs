using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using NimBus.Extensions.IntegrationIntelligence;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Creates stores from the host's already resolved provider settings.</summary>
public static class IntelligenceSettingsStore
{
    public static IIntelligenceSettingsStore Create(IIntegrationIntelligenceStorageSettings storage)
        => string.Equals(storage.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase)
            ? new SqlIntelligenceSettingsStore(storage.SqlConnectionString ?? string.Empty)
            : new CosmosIntelligenceSettingsStore(storage.CosmosClient ?? throw new InvalidOperationException("Cosmos is not configured."),
                storage.CosmosDatabaseName ?? "MessageDatabase");
}

/// <summary>One revision-fenced SQL settings row; reads never create schema.</summary>
public sealed class SqlIntelligenceSettingsStore(string connectionString) : IIntelligenceSettingsStore
{
    public async Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingsJson FROM dbo.IntelligenceAdminSettings WHERE Id=1";
        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string json
                ? JsonConvert.DeserializeObject<IntelligenceSettingsDocument>(json) ?? throw new InvalidDataException("Invalid settings record.")
                : null;
        }
        catch (SqlException exception) when (exception.Number == 208) { return null; }
    }

    public async Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @lock int;
            EXEC @lock=sp_getapplock @Resource='NimBus.Intelligence.AdminSettings', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;
            IF @lock<0 THROW 51000, 'Settings lock unavailable', 1;
            IF OBJECT_ID('dbo.IntelligenceAdminSettings','U') IS NULL
                CREATE TABLE dbo.IntelligenceAdminSettings (Id int NOT NULL PRIMARY KEY CHECK(Id=1), Revision varchar(36) NOT NULL, SettingsJson nvarchar(max) NOT NULL);
            IF EXISTS(SELECT 1 FROM dbo.IntelligenceAdminSettings WHERE Id=1 AND Revision=@expected)
            BEGIN
                UPDATE dbo.IntelligenceAdminSettings SET Revision=@revision,SettingsJson=@json WHERE Id=1 AND Revision=@expected;
                SELECT 1;
            END
            ELSE IF @expected='none' AND NOT EXISTS(SELECT 1 FROM dbo.IntelligenceAdminSettings WHERE Id=1)
            BEGIN
                INSERT dbo.IntelligenceAdminSettings(Id,Revision,SettingsJson) VALUES(1,@revision,@json);
                SELECT 1;
            END
            ELSE SELECT 0;
            """;
        command.Parameters.AddWithValue("@expected", expectedRevision);
        command.Parameters.AddWithValue("@revision", document.Revision);
        command.Parameters.AddWithValue("@json", JsonConvert.SerializeObject(document));
        var saved = (int)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }
}

/// <summary>One conditional Cosmos item; container provisioning belongs to deployment, not runtime.</summary>
public sealed class CosmosIntelligenceSettingsStore(CosmosClient client, string databaseName) : IIntelligenceSettingsStore
{
    private const string Id = "failure-classification";
    private readonly Container _container = client.GetContainer(databaseName, "intelligencesettings");

    public async Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _container.ReadItemAsync<SettingsItem>(Id, new PartitionKey(Id), cancellationToken: cancellationToken)).Resource.Document
                ?? throw new InvalidDataException("Invalid settings record.");
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken)
    {
        try
        {
            if (expectedRevision == "none")
                await _container.CreateItemAsync(new SettingsItem { Document = document }, new PartitionKey(Id), cancellationToken: cancellationToken);
            else
            {
                var current = await _container.ReadItemAsync<SettingsItem>(Id, new PartitionKey(Id), cancellationToken: cancellationToken);
                if (current.Resource.Document.Revision != expectedRevision) return false;
                await _container.ReplaceItemAsync(new SettingsItem { Document = document }, Id, new PartitionKey(Id),
                    new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
            }
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) { return false; }
    }

    private sealed class SettingsItem
    {
        [JsonProperty("id")]
        public string Key { get; set; } = Id;
        public IntelligenceSettingsDocument Document { get; set; } = null!;
    }
}
