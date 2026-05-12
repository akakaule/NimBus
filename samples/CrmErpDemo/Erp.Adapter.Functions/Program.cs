using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Erp.Adapter.Functions.HandoffMode;
using Erp.Adapter.Functions.Handlers;
using Erp.Adapter.Functions.Pipeline;
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

static string ResolveErpApiBaseUrl(IConfiguration cfg) =>
    cfg["services:erp-api:https:0"]
        ?? cfg["services:erp-api:http:0"]
        ?? cfg["Erp:ApiBaseUrl"]
        ?? throw new InvalidOperationException("Erp API base URL is required (service discovery or Erp:ApiBaseUrl).");

builder.Services.AddHttpClient<IErpApiClient, ErpApiClient>(c =>
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration)));

builder.Services.AddHttpClient<IServiceModeClient, ServiceModeClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IHandoffModeClient, HandoffModeClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IHandoffJobRegistration, HandoffJobRegistration>(c =>
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration)));

builder.Services.AddNimBus(n =>
{
    // Pure subscriber/dispatcher Functions worker — no NimBus message store needed.
    n.WithoutStorageProvider();
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
    n.AddPipelineBehavior<ServiceModeMiddleware>();
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
