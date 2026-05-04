using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Broker.Services;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.Resolver
{
    /// <summary>
    /// Extension methods to register Resolver services via the NimBus builder.
    /// Storage provider registration is the consumer's responsibility — call
    /// AddCosmosDbMessageStore() or AddSqlServerMessageStore() in the host
    /// composition root before AddResolver().
    /// </summary>
    public static class ResolverBuilderExtensions
    {
        /// <summary>
        /// Adds the Resolver services (Service Bus listener + message handling) to the
        /// NimBus builder. The active storage provider must already be registered.
        /// </summary>
        public static INimBusBuilder AddResolver(this INimBusBuilder builder)
        {
            var services = builder.Services;

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
                var sessionStateStore = sp.GetService<NimBus.MessageStore.Abstractions.ISessionStateStore>();
                return new ServiceBusAdapter(messageHandler, serviceBusClient, entityPath, sessionStateStore);
            });

            return builder;
        }
    }
}
