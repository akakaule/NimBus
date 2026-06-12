using System;
using System.Net.Http;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using NimBus.Broker.Services;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            services.AddFlowStateChangeNotifier();

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

            return services;
        }

        /// <summary>
        /// Registers the Resolver write-path state-change notifier (spec 020).
        /// When <c>NimBus:Flow:WebAppUrl</c> is configured the Resolver pushes
        /// <c>endpointupdate</c> broadcasts to the management WebApp's storage-hook
        /// webhook (driving the live Flow / Monitor pages for storage providers
        /// without a Change Feed). Absent that config it registers the no-op
        /// notifier, so existing deployments are unaffected (spec NFR-004).
        /// </summary>
        internal static IServiceCollection AddFlowStateChangeNotifier(this IServiceCollection services)
        {
            services.AddSingleton<IMessageStateChangeNotifier>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var webAppUrl = config.GetValue<string>("NimBus:Flow:WebAppUrl");
                if (string.IsNullOrWhiteSpace(webAppUrl)
                    || !Uri.TryCreate(EnsureTrailingSlash(webAppUrl), UriKind.Absolute, out var baseUri))
                {
                    return new NoopMessageStateChangeNotifier();
                }

                var webhookKey = config.GetValue<string>("EventGrid:WebhookKey");
                var httpClient = new HttpClient
                {
                    BaseAddress = baseUri,
                    Timeout = TimeSpan.FromSeconds(10),
                };
                var logger = sp.GetService<ILogger<HttpEndpointStateChangeNotifier>>();
                return new HttpEndpointStateChangeNotifier(httpClient, webhookKey, logger);
            });

            return services;
        }

        private static string EnsureTrailingSlash(string url) =>
            url.EndsWith('/') ? url : url + "/";
    }
}
