using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.MessageStore;
using NimBus.MessageStore.SqlServer;
using NimBus.Resolver;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

// The Resolver as a plain worker: the same ResolverService and stores as the Functions app, with a
// NimBus session receiver in place of the Functions Service Bus trigger. Configuration matches the
// Functions app: ResolverId (default "Resolver"), ConnectionStrings:servicebus or
// AzureWebJobsServiceBus, NimBus:StorageProvider and the store's connection settings.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

var resolverId = builder.Configuration["ResolverId"];
if (string.IsNullOrWhiteSpace(resolverId))
{
    resolverId = "Resolver";
    builder.Configuration["ResolverId"] = resolverId;
}

var storageProvider = ResolverStorageProvider.Select(builder.Configuration);
builder.Services.AddNimBus(nimbus =>
{
    if (ResolverStorageProvider.IsSqlServer(storageProvider))
    {
        nimbus.AddSqlServerMessageStore();
    }
    else
    {
        nimbus.AddCosmosDbMessageStore();
    }
});

builder.Services.AddResolver();

var maxConcurrentSessions = builder.Configuration.GetValue("Resolver:MaxConcurrentSessions", 8);
builder.Services.AddSingleton<IHostedService>(sp => new NimBusReceiverHostedService(
    sp.GetRequiredService<ServiceBusClient>(),
    sp.GetRequiredService<IServiceBusAdapter>(),
    new NimBusReceiverOptions
    {
        TopicName = resolverId,
        SubscriptionName = resolverId,
        MaxConcurrentSessions = maxConcurrentSessions,
    },
    sp.GetRequiredService<ILogger<NimBusReceiverHostedService>>()));

builder.Build().Run();
