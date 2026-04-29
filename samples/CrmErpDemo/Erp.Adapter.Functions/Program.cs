using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Erp.Adapter.Functions.Handlers;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;

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

builder.Services.AddHttpClient<IErpApiClient, ErpApiClient>(c =>
{
    var cfg = builder.Configuration["services:erp-api:https:0"]
        ?? builder.Configuration["services:erp-api:http:0"]
        ?? builder.Configuration["Erp:ApiBaseUrl"]
        ?? throw new InvalidOperationException("Erp API base URL is required (service discovery or Erp:ApiBaseUrl).");
    c.BaseAddress = new Uri(cfg);
});

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<MetricsMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber("ErpEndpoint", sub =>
{
    sub.AddHandler<CrmAccountCreated, CrmAccountCreatedHandler>();
    sub.AddHandler<CrmAccountUpdated, CrmAccountUpdatedHandler>();
    sub.AddHandler<CrmAccountDeleted, CrmAccountDeletedHandler>();
    sub.AddHandler<CrmContactCreated, CrmContactCreatedHandler>();
    sub.AddHandler<CrmContactUpdated, CrmContactUpdatedHandler>();
    sub.AddHandler<CrmContactDeleted, CrmContactDeletedHandler>();
});

// DeferredMessageProcessor is used by the deferred-processor function.
builder.Services.AddSingleton<IDeferredMessageProcessor>(sp =>
    new DeferredMessageProcessor(sp.GetRequiredService<ServiceBusClient>()));

builder.Build().Run();
