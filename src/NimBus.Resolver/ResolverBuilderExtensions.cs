using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Broker.Services;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using Serilog;

namespace NimBus.Resolver
{
    /// <summary>
    /// Extension methods to register Resolver services via the NimBus builder.
    /// </summary>
    public static class ResolverBuilderExtensions
    {
        /// <summary>
        /// Adds the Resolver services (Cosmos DB, Service Bus, message handling) to the NimBus builder.
        /// </summary>
        public static INimBusBuilder AddResolver(this INimBusBuilder builder)
        {
            var services = builder.Services;

            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var endpoint = config.GetValue<string>("CosmosAccountEndpoint");
                if (!string.IsNullOrEmpty(endpoint) && !endpoint.Contains("AccountKey="))
                    return new CosmosClient(endpoint, new DefaultAzureCredential());

                var connectionString = endpoint
                    ?? config.GetConnectionString("cosmos")
                    ?? config.GetValue<string>("CosmosConnection")
                    ?? throw new InvalidOperationException("CosmosConnection configuration is required");
                return new CosmosClient(connectionString);
            });

            services.AddSingleton<ICosmosDbClient>(sp =>
            {
                var cosmosClient = sp.GetRequiredService<CosmosClient>();
                return new CosmosDbClient(cosmosClient, Log.Logger);
            });

            services.AddSingleton<IMessageHandler, ResolverService>();

            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var fqns = config.GetValue<string>("AzureWebJobsServiceBus__fullyQualifiedNamespace");
                if (!string.IsNullOrEmpty(fqns) && !fqns.Contains("SharedAccessKey="))
                    return new ServiceBusClient(fqns, new DefaultAzureCredential());

                var connectionString = fqns
                    ?? config.GetConnectionString("servicebus")
                    ?? config.GetValue<string>("AzureWebJobsServiceBus")
                    ?? throw new InvalidOperationException("AzureWebJobsServiceBus configuration is required");
                return new ServiceBusClient(connectionString);
            });

            services.AddSingleton<IServiceBusAdapter>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var resolverId = config.GetValue<string>("ResolverId")
                    ?? throw new InvalidOperationException("ResolverId configuration is required");
                var messageHandler = sp.GetRequiredService<IMessageHandler>();
                var serviceBusClient = sp.GetRequiredService<ServiceBusClient>();
                var entityPath = $"{resolverId}/{resolverId}";
                return new ServiceBusAdapter(messageHandler, serviceBusClient, entityPath);
            });

            return builder;
        }
    }
}
