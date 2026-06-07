using NimBus.Core.Extensions;
using NimBus.MessageStore;
using NimBus.MessageStore.SqlServer;
using NimBus.MappingExecutor;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Storage provider: same toggle as the rest of the CrmErpDemo AppHost.
// NimBus__StorageProvider (or StorageProvider) selects sqlserver (default) or cosmos.
// AddSqlServerMessageStore / AddCosmosDbMessageStore registers IEventMappingStore
// and IEventSchemaStore which MappingExecutorHandler depends on.
var storageProvider = (
        builder.Configuration["NimBus:StorageProvider"]
        ?? builder.Configuration["StorageProvider"]
        ?? "sqlserver")
    .ToLowerInvariant();

builder.Services.AddNimBus(nimbus =>
{
    if (storageProvider == "cosmos")
        nimbus.AddCosmosDbMessageStore();
    else
        nimbus.AddSqlServerMessageStore();
});

// Register the Mapping Executor: subscriber on MappingZoneEndpoint with a dynamic
// fallback handler (MappingExecutorHandler) + publisher + deferred processor.
// Mirrors CrmErpDemo.AgentZone's AddNimBusSubscriber + AddNimBusReceiver pattern.
builder.Services.AddMappingExecutor("MappingZoneEndpoint");

builder.Build().Run();
