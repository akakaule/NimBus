using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.Events;
using DataPlatform.Adapter.Functions.Handlers;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.SDK.Extensions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddServiceDefaults();

builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

// ServiceBusClient is provided from the AzureWebJobsServiceBus connection string.
builder.Services.AddSingleton<ServiceBusClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connection = cfg["AzureWebJobsServiceBus"]
        ?? throw new InvalidOperationException("AzureWebJobsServiceBus is required.");
    return new ServiceBusClient(connection);
});

// Register EnrichedContactHandler for DI resolution.
builder.Services.AddTransient<EnrichedContactHandler>();

builder.Services.AddNimBusSubscriber("DataPlatformEndpoint", sub =>
{
    sub.AddHandler<ErpCustomerCreated, ErpCustomerCreatedHandler>();

    // Spec 022 Phase 3 Task D — consume the AI-agent enriched-contact event on this endpoint.
    // The event has no compiled IEvent class; it is identified only by its EventTypeId string.
    // Use the DI-aware overload so EnrichedContactHandler receives its ILogger from the container.
    sub.AddDynamicHandler("crm.contact.enriched.v1", sp => sp.GetRequiredService<EnrichedContactHandler>());
});

builder.Build().Run();
