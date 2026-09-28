using D365Sales.Adapter.Clients;
using D365Sales.Adapter.Handlers;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;

// Dynamics 365 Sales adapter: receives Business Central's customer, quote and order events on
// D365SalesEndpoint and writes them to Dataverse (here the simulator's Dataverse-shaped API).
// Messages about one customer share a session, so they are applied in the order BC raised them.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var d365ApiBaseUrl = builder.Configuration["services:d365-api:https:0"]
    ?? builder.Configuration["services:d365-api:http:0"]
    ?? builder.Configuration["Dynamics365:ApiBaseUrl"]
    ?? throw new InvalidOperationException("The Dynamics 365 API base URL is required (service discovery or Dynamics365:ApiBaseUrl).");

builder.Services.AddHttpClient<IDataverseClient, DataverseClient>(client => client.BaseAddress = new Uri(d365ApiBaseUrl));

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber(
    configure: options => options.Endpoint = "D365SalesEndpoint",
    configureBuilder: sub => sub.AddHandlersFromAssemblyContaining<BcSalesQuoteCreatedHandler>());

builder.Services.AddNimBusReceiver(options =>
{
    options.TopicName = "D365SalesEndpoint";
    options.SubscriptionName = "D365SalesEndpoint";
    options.MaxConcurrentSessions = 16;
    options.SessionIdleTimeout = TimeSpan.FromSeconds(3);
});

// Worker hosts own the deferred-replay loop (parked messages behind a blocked session).
builder.Services.AddNimBusDeferredProcessorHostedService("D365SalesEndpoint");

builder.Build().Run();
