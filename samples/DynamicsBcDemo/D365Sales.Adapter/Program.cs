using D365Sales.Adapter.Clients;
using D365Sales.Adapter.Handlers;
using D365Sales.Adapter.Resilience;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Pipeline;
using NimBus.Inbox.SqlServer;
using NimBus.SDK.Extensions;

// Dynamics 365 Sales adapter: receives Business Central's customer, contact, product-group and quote events on
// D365SalesEndpoint and writes them to Dataverse (here the simulator's Dataverse-shaped API).
// Messages about one customer share a session, so they are applied in the order BC raised them.
// Its failure handling follows the same shape as the Business Central adapter: typed failures,
// NimBus retry rules, a circuit breaker, an inbox and no hidden HTTP retries.
var builder = Host.CreateApplicationBuilder(args);

// No UseStandardResilienceHandler: the Dataverse client must not retry inside a NimBus attempt.
// Retries belong to the NimBus rules below, where each one is audited.
builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var d365ApiBaseUrl = builder.Configuration["services:d365-api:https:0"]
    ?? builder.Configuration["services:d365-api:http:0"]
    ?? builder.Configuration["Dynamics365:ApiBaseUrl"]
    ?? throw new InvalidOperationException("The Dynamics 365 API base URL is required (service discovery or Dynamics365:ApiBaseUrl).");

var resilience = builder.Configuration.GetSection("Dynamics365:Resilience").Get<D365ResilienceOptions>() ?? new D365ResilienceOptions();

builder.Services.AddHttpClient<IDataverseClient, DataverseClient>(client =>
{
    client.BaseAddress = new Uri(d365ApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Inbox deduplication lives in the integration platform's own database, never in Dataverse. Its own
// table keeps it apart from the Business Central adapter's inbox in the same database.
var nimbusDbConnectionString = builder.Configuration.GetConnectionString("nimbus")
    ?? throw new InvalidOperationException("ConnectionStrings:nimbus is required for the inbox deduplication store.");
builder.Services.AddNimBusSqlServerInbox(options =>
{
    options.ConnectionString = nimbusDbConnectionString;
    options.TableName = "D365SalesInboxMessages";
});

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber(
    configure: options => options.Endpoint = "D365SalesEndpoint",
    configureBuilder: sub =>
    {
        sub.AddHandlersFromAssemblyContaining<BcSalesQuoteCreatedHandler>();
        sub.UseInbox(inbox =>
        {
            inbox.DeduplicationStore = InboxStore.SqlServer;
            inbox.RetentionPeriod = TimeSpan.FromDays(2);
            inbox.CleanupInterval = TimeSpan.FromMinutes(15);
        });
        sub.ConfigureRetryPolicies(policies => D365Resilience.ConfigureRetries(policies, resilience));
        sub.WithCircuitBreaker(circuit => D365Resilience.ConfigureCircuitBreaker(circuit, resilience));
    });

builder.Services.AddNimBusReceiver(options =>
{
    options.TopicName = "D365SalesEndpoint";
    options.SubscriptionName = "D365SalesEndpoint";
    options.MaxConcurrentSessions = 16;
    options.SessionIdleTimeout = TimeSpan.FromSeconds(3);
    // No prefetch: with the circuit breaker, prefetched messages would burn delivery attempts on
    // every open cycle.
    options.PrefetchCount = 0;
});

// Worker hosts own the deferred-replay loop (parked messages behind a blocked session).
builder.Services.AddNimBusDeferredProcessorHostedService("D365SalesEndpoint");

builder.Build().Run();
