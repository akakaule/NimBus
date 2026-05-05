using System;
using Azure.Messaging.ServiceBus;
using NimBus.Broker.Services;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace NimBus.Resolver
{
    public static class ServiceExtensions
    {
        /// <summary>
        /// Legacy IServiceCollection-based registration. Storage and transport
        /// providers must be registered separately via the NimBus builder
        /// (AddCosmosDbMessageStore / AddSqlServerMessageStore +
        /// AddServiceBusTransport) — the resolved <see cref="ServiceBusClient"/>
        /// comes from <c>AddServiceBusTransport</c>.
        /// </summary>
        public static IServiceCollection AddResolver(this IServiceCollection services)
        {
            services.AddSingleton<Serilog.ILogger>(Log.Logger);

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

            return services;
        }
    }
}
