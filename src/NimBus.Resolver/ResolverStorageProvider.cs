using System;
using Microsoft.Extensions.Configuration;

namespace NimBus.Resolver
{
    /// <summary>
    /// Chooses the Resolver's message-store provider from configuration.
    /// </summary>
    internal static class ResolverStorageProvider
    {
        internal const string SqlServer = "sqlserver";
        internal const string Cosmos = "cosmos";

        /// <summary>
        /// Provider selection mirrors the WebApp logic: explicit StorageProvider config wins,
        /// otherwise auto-detect from which connection settings are present, defaulting to
        /// Cosmos for backwards compatibility.
        /// </summary>
        internal static string Select(IConfiguration configuration)
        {
            var storageProvider = configuration.GetValue<string>("NimBus:StorageProvider")
                ?? configuration.GetValue<string>("StorageProvider");
            if (!string.IsNullOrWhiteSpace(storageProvider))
                return storageProvider;

            var hasSqlConfig = !string.IsNullOrWhiteSpace(configuration.GetValue<string>("SqlConnection"))
                || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("sqlserver"))
                || !string.IsNullOrWhiteSpace(configuration.GetValue<string>("SqlServerConnection"));
            var hasCosmosConfig = !string.IsNullOrWhiteSpace(configuration.GetValue<string>("CosmosAccountEndpoint"))
                || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("cosmos"))
                || !string.IsNullOrWhiteSpace(configuration.GetValue<string>("CosmosConnection"));
            return (hasSqlConfig && !hasCosmosConfig) ? SqlServer : Cosmos;
        }

        /// <summary>True when <paramref name="storageProvider"/> selects SQL Server.</summary>
        internal static bool IsSqlServer(string storageProvider) =>
            string.Equals(storageProvider, SqlServer, StringComparison.OrdinalIgnoreCase);
    }
}
