using System;
using Azure.Identity;
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
        /// Legacy IServiceCollection-based registration. Storage provider must be
        /// registered separately via NimBus builder (AddCosmosDbMessageStore /
        /// AddSqlServerMessageStore).
        /// </summary>
        public static IServiceCollection AddResolver(this IServiceCollection services)
        {
            services.AddSingleton<Serilog.ILogger>(Log.Logger);

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

            return services;
        }
    }
}
