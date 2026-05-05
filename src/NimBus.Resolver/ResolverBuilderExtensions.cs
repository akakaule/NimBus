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
    /// composition root before AddResolver(). Transport-provider registration
    /// (e.g. AddServiceBusTransport()) is also a prerequisite — the resolved
    /// ServiceBusClient comes from there.
    /// </summary>
    public static class ResolverBuilderExtensions
    {
        /// <summary>
        /// Adds the Resolver services (message handling + Service Bus receive
        /// adapter) to the NimBus builder. Caller must register the storage
        /// provider and AddServiceBusTransport() before this call.
        /// </summary>
        public static INimBusBuilder AddResolver(this INimBusBuilder builder)
        {
            var services = builder.Services;

            services.AddSingleton<IMessageHandler, ResolverService>();

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
