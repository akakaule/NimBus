using Azure.Messaging.ServiceBus;
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
using NimBus.Core.Pipeline;
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

builder.Services.AddHttpClient<IProcessingDelayClient, ProcessingDelayClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IHandoffJobRegistration, HandoffJobRegistration>(c =>
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration)));

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
    n.AddPipelineBehavior<ServiceModeMiddleware>();
    // Runs after the service-mode gate: don't delay messages that are being rejected,
    // but hold the rest for the configured time before their handler runs.
    n.AddPipelineBehavior<ProcessingDelayMiddleware>();
});

// The deferred-processor BackgroundService is intentionally NOT registered here —
// ErpDeferredProcessorFunction's [ServiceBusTrigger] owns the trigger subscription
// directly. The shared dispatch body lives in NimBus.SDK.Hosting.DeferredMessageDispatcher.
builder.Services.AddNimBusSubscriber("ErpEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<CrmAccountCreatedHandler>();
});

builder.Build().Run();
