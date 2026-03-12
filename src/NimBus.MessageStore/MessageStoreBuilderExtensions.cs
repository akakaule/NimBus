using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;

namespace NimBus.MessageStore
{
    /// <summary>
    /// Extension methods to register MessageStore services via the NimBus builder.
    /// </summary>
    public static class MessageStoreBuilderExtensions
    {
        /// <summary>
        /// Adds Cosmos DB-backed message store services to the NimBus builder.
        /// The CosmosClient must be registered separately (or via AddCosmosClient).
        /// </summary>
        public static INimBusBuilder AddMessageStore(this INimBusBuilder builder)
        {
            builder.Services.AddSingleton<ICosmosDbClient>(sp =>
            {
                var cosmosClient = sp.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>();
                return new CosmosDbClient(cosmosClient);
            });

            return builder;
        }
    }
}
